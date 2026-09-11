using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SteamStat.Core.Http;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class HttpClientConfigurationTests
{
    [Test]
    public void NamedClients_AreDependencyIsolatedAndUseResilienceTimeouts()
    {
        using var provider = new ServiceCollection().AddSteamStatCore().BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        var store = factory.CreateClient(SteamStatHttpClients.SteamStore);
        var webApi = factory.CreateClient(SteamStatHttpClients.SteamWebApi);
        var cdn = factory.CreateClient(SteamStatHttpClients.SteamCdn);
        var download = factory.CreateClient(SteamStatHttpClients.Download);

        new[] { store, webApi, cdn, download }
            .Should().OnlyContain(client => client.Timeout == Timeout.InfiniteTimeSpan);
        store.BaseAddress.Should().Be("https://store.steampowered.com/");
        webApi.BaseAddress.Should().Be("https://api.steampowered.com/");
        cdn.BaseAddress.Should().Be("https://avatars.akamai.steamstatic.com/");
        download.BaseAddress.Should().BeNull();
    }

    [Test]
    public async Task Resilience_RetriesJsonTransientFailuresButNotClientErrorsOrDownloads()
    {
        var storeHandler = new SequenceHandler(
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.OK,
            HttpStatusCode.ServiceUnavailable);
        var webApiHandler = new SequenceHandler(HttpStatusCode.BadRequest);
        var cdnHandler = new SequenceHandler(HttpStatusCode.ServiceUnavailable);
        var services = new ServiceCollection().AddSteamStatCore(options =>
        {
            options.RetryCount = 1;
            options.RetryDelay = TimeSpan.FromMilliseconds(1);
        });
        services.AddHttpClient(SteamStatHttpClients.SteamStore)
            .ConfigurePrimaryHttpMessageHandler(() => storeHandler);
        services.AddHttpClient(SteamStatHttpClients.SteamWebApi)
            .ConfigurePrimaryHttpMessageHandler(() => webApiHandler);
        services.AddHttpClient(SteamStatHttpClients.SteamCdn)
            .ConfigurePrimaryHttpMessageHandler(() => cdnHandler);
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        using var storeResponse = await SendAsync(factory, SteamStatHttpClients.SteamStore, "appdetails");
        using var unsafeResponse = await SendAsync(
            factory, SteamStatHttpClients.SteamStore, "write-test", HttpMethod.Post);
        using var webApiResponse = await SendAsync(factory, SteamStatHttpClients.SteamWebApi, "wishlist");
        using var cdnResponse = await SendAsync(factory, SteamStatHttpClients.SteamCdn, "avatar-download");

        storeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        unsafeResponse.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        storeHandler.Calls.Should().Be(3);
        webApiResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        webApiHandler.Calls.Should().Be(1);
        cdnResponse.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        cdnHandler.Calls.Should().Be(1);
    }

    [Test]
    public async Task RequestQuota_IsPartitionedByDependencyAndOperation()
    {
        var handler = new SequenceHandler(Enumerable.Repeat(HttpStatusCode.OK, 4).ToArray());
        var services = new ServiceCollection().AddSteamStatCore(options =>
        {
            options.RequestTokenLimit = 1;
            options.RequestTokensPerPeriod = 1;
            options.RequestReplenishmentPeriod = TimeSpan.FromHours(1);
            options.HttpQueueLimit = 0;
        });
        services.AddHttpClient(SteamStatHttpClients.SteamStore)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        services.AddHttpClient(SteamStatHttpClients.SteamWebApi)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        services.AddHttpClient(SteamStatHttpClients.SteamCdn)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        using var firstStore = await SendAsync(factory, SteamStatHttpClients.SteamStore, "appdetails");
        var rejected = () => SendAsync(factory, SteamStatHttpClients.SteamStore, "appdetails");
        using var otherStoreOperation = await SendAsync(factory, SteamStatHttpClients.SteamStore, "search");
        using var webApi = await SendAsync(factory, SteamStatHttpClients.SteamWebApi, "appdetails");
        using var cdn = await SendAsync(factory, SteamStatHttpClients.SteamCdn, "asset-download");

        firstStore.StatusCode.Should().Be(HttpStatusCode.OK);
        await rejected.Should().ThrowAsync<SteamRateLimiterRejectedException>();
        otherStoreOperation.StatusCode.Should().Be(HttpStatusCode.OK);
        webApi.StatusCode.Should().Be(HttpStatusCode.OK);
        cdn.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task CdnQuota_HasBoundedCancelableQueueAndDoesNotBlockStore()
    {
        var cdnHandler = new BlockingHandler();
        var storeHandler = new SequenceHandler(HttpStatusCode.OK);
        var services = new ServiceCollection().AddSteamStatCore(options =>
        {
            options.CdnConcurrencyLimit = 1;
            options.CdnQueueLimit = 1;
        });
        services.AddHttpClient(SteamStatHttpClients.SteamCdn)
            .ConfigurePrimaryHttpMessageHandler(() => cdnHandler);
        services.AddHttpClient(SteamStatHttpClients.SteamStore)
            .ConfigurePrimaryHttpMessageHandler(() => storeHandler);
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var first = SendAsync(factory, SteamStatHttpClients.SteamCdn, "asset-download");
        using var cancellation = new CancellationTokenSource();
        var queued = SendAsync(
            factory, SteamStatHttpClients.SteamCdn, "asset-download", cancellationToken: cancellation.Token);
        await Task.Delay(20);

        var rejected = () => SendAsync(factory, SteamStatHttpClients.SteamCdn, "asset-download");
        await rejected.Should().ThrowAsync<SteamRateLimiterRejectedException>();
        using var store = await SendAsync(factory, SteamStatHttpClients.SteamStore, "appdetails");
        store.StatusCode.Should().Be(HttpStatusCode.OK);

        cancellation.Cancel();
        await FluentActions.Awaiting(() => queued).Should().ThrowAsync<OperationCanceledException>();
        cdnHandler.Complete();
        using var firstResponse = await first;
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static Task<HttpResponseMessage> SendAsync(
        IHttpClientFactory factory,
        string clientName,
        string operation,
        HttpMethod? method = null,
        CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, "test");
        SteamHttpRequestOptions.SetOperation(request, operation);
        return factory.CreateClient(clientName).SendAsync(request, cancellationToken);
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<HttpResponseMessage> _response =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete() => _response.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => _response.Task.WaitAsync(cancellationToken);
    }

    private sealed class SequenceHandler(params HttpStatusCode[] statusCodes) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Math.Min(Interlocked.Increment(ref _calls) - 1, statusCodes.Length - 1);
            return Task.FromResult(new HttpResponseMessage(statusCodes[index]));
        }
    }
}
