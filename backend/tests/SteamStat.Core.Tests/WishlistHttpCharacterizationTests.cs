using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Core.Steam.Gateway;
using SteamStat.Core.Steam.Gateway.Internal;
using SteamStat.Core.Steam.Session;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class WishlistHttpCharacterizationTests
{
    [Test]
    public async Task WishlistHttp_SuccessReturnsAppIdsThroughSourceAdapter()
    {
        var handler = new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"response\":{\"items\":[{\"appid\":10},{\"appid\":20}]}}",
                Encoding.UTF8,
                "application/json")
        });

        var result = await CreateSource(handler).GetAsync(76561198000000000, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Equal(10u, 20u);
        result.Source.Should().Be(SteamDataSource.Http);
        handler.RequestUri.Should().Be(
            "https://api.steampowered.com/IWishlistService/GetWishlist/v1/?steamid=76561198000000000");
    }

    [Test]
    public async Task WishlistHttp_SuccessWithoutItemsReturnsSuccessfulEmpty()
    {
        var handler = new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"response\":{}}", Encoding.UTF8, "application/json")
        });

        var result = await CreateSource(handler).GetAsync(76561198000000000, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Test]
    public async Task WishlistHttp_FailureReturnsTypedFailure()
    {
        var result = await CreateSource(
                new FakeHandler(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))
            .GetAsync(76561198000000000, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(SteamFailureKind.Transient);
    }

    [Test]
    public async Task WishlistHttp_PropagatesCallerCancellationToSource()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var handler = new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK));

        var action = () => CreateSource(handler)
            .GetAsync(76561198000000000, cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        handler.CancellationToken.IsCancellationRequested.Should().BeTrue();
    }

    private static HttpWishlistSource CreateSource(HttpMessageHandler handler) => new(
        new FakeHttpClientFactory(handler),
        new SteamResultClassifier(),
        TimeProvider.System,
        NullLogger<HttpWishlistSource>.Instance);

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false)
        {
            BaseAddress = name == SteamStat.Core.Http.SteamStatHttpClients.SteamWebApi
                ? new Uri("https://api.steampowered.com/")
                : null
        };
    }

    private sealed class FakeHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }
        public CancellationToken CancellationToken { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            CancellationToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response);
        }
    }
}
