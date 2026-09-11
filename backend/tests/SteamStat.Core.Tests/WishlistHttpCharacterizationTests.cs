using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Core.Features.Library;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class WishlistHttpCharacterizationTests
{
    [Test]
    public async Task WishlistHttp_SuccessReturnsAppIdsThroughInjectedHandler()
    {
        var handler = new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"response\":{\"items\":[{\"appid\":10},{\"appid\":20}]}}", Encoding.UTF8, "application/json")
        });
        var service = CreateService(handler);

        var result = await service.FetchWishlistAppIdsAsync(76561198000000000);

        result.Should().Equal(10, 20);
        handler.RequestUri.Should().Be("https://api.steampowered.com/IWishlistService/GetWishlist/v1/?steamid=76561198000000000");
    }

    [Test]
    public async Task WishlistHttp_SuccessWithoutItemsReturnsEmpty()
    {
        var handler = new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"response\":{}}", Encoding.UTF8, "application/json")
        });

        var result = await CreateService(handler).FetchWishlistAppIdsAsync(76561198000000000);

        result.Should().BeEmpty();
    }

    [Test]
    public async Task WishlistHttp_FailureReturnsEmpty()
    {
        var result = await CreateService(new FakeHandler(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))
            .FetchWishlistAppIdsAsync(76561198000000000, CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Test]
    public async Task WishlistHttp_PropagatesCallerCancellationToSource()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var handler = new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK));

        var action = () => CreateService(handler)
            .FetchWishlistAppIdsAsync(76561198000000000, cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        handler.CancellationToken.IsCancellationRequested.Should().BeTrue();
    }

    private static SteamLibraryService CreateService(HttpMessageHandler handler) => new(
        null!, null!, null!, null!, new FakeHttpClientFactory(handler), null!, TimeProvider.System,
        NullLogger<SteamLibraryService>.Instance);

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

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            CancellationToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response);
        }
    }
}
