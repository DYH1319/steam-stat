using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Core.Features.Apps.Contracts;
using SteamStat.Core.Steam.Cache;
using SteamStat.Core.Steam.Gateway;
using SteamStat.Core.Steam.Gateway.Internal;
using SteamStat.Core.Steam.Session;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class SteamAppCatalogGatewayTests
{
    [Test]
    public async Task HttpSource_DistinguishesSuccessNotFoundAndFailure()
    {
        var time = new FixedTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var success = new HttpStoreSource(
            new FakeHttpClientFactory(Json(HttpStatusCode.OK,
                "{\"730\":{\"success\":true,\"data\":{\"name\":\"Counter-Strike 2\",\"type\":\"game\",\"is_free\":true}}}")),
            new SteamResultClassifier(), time, NullLogger<HttpStoreSource>.Instance);
        var notFound = new HttpStoreSource(
            new FakeHttpClientFactory(Json(HttpStatusCode.OK, "{\"999999999\":{\"success\":false}}")),
            new SteamResultClassifier(), time, NullLogger<HttpStoreSource>.Instance);
        var unavailable = new HttpStoreSource(
            new FakeHttpClientFactory(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
            new SteamResultClassifier(), time, NullLogger<HttpStoreSource>.Instance);

        var successfulResult = await success.GetAsync(730, string.Empty, null, CancellationToken.None);
        var notFoundResult = await notFound.GetAsync(999999999, string.Empty, null, CancellationToken.None);
        var failureResult = await unavailable.GetAsync(730, string.Empty, null, CancellationToken.None);

        successfulResult.Should().BeEquivalentTo(new
        {
            IsSuccess = true,
            Value = new SteamAppMetadataSnapshot(730, "Counter-Strike 2", "game", true),
            Source = (SteamDataSource?)SteamDataSource.Http,
            Freshness = (SteamFreshness?)SteamFreshness.Fresh
        });
        notFoundResult.IsSuccess.Should().BeFalse();
        notFoundResult.Failure.Should().Be(SteamFailureKind.NotFound);
        failureResult.IsSuccess.Should().BeFalse();
        failureResult.Failure.Should().Be(SteamFailureKind.Transient);
    }

    [Test]
    public async Task Gateway_PersistsSuccessfulMetadataAndReadsItAfterGatewayRestart()
    {
        var time = new FixedTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var store = new MemoryCacheStore();
        var firstSource = new FakeSource(SteamGatewayResult<SteamAppMetadataSnapshot>.Succeeded(
            new SteamAppMetadataSnapshot(730, "Counter-Strike 2", "game", true),
            SteamDataSource.Http, SteamFreshness.Fresh, time.GetUtcNow(), time.GetUtcNow()));
        using (var firstGateway = CreateGateway(store, firstSource, time))
        {
            (await firstGateway.GetAppAsync(730, " SCHINESE ")).IsSuccess.Should().BeTrue();
        }
        var failingSource = new FakeSource(SteamGatewayResult<SteamAppMetadataSnapshot>.Failed(
            SteamFailureKind.Offline, "http_connection_error"));

        using var restartedGateway = CreateGateway(store, failingSource, time);
        var result = await restartedGateway.GetAppAsync(730, "schinese");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Name.Should().Be("Counter-Strike 2");
        result.Source.Should().Be(SteamDataSource.Sqlite);
        result.Freshness.Should().Be(SteamFreshness.Fresh);
        failingSource.Calls.Should().Be(0);
    }

    [Test]
    public async Task Gateway_UpstreamFailureReturnsStaleCacheWithFailureMetadata()
    {
        var fetchedAt = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var time = new FixedTimeProvider(fetchedAt);
        var key = SteamCacheKey.Create("app-metadata", "public", "730");
        var store = new MemoryCacheStore
        {
            Entry = SteamResourcePolicies.AppMetadata.CreateEntry(
                key,
                "{\"AppId\":730,\"Name\":\"Cached Name\",\"Type\":\"game\",\"IsFree\":false}",
                SteamDataSource.Http,
                fetchedAt)
        };
        var source = new FakeSource(SteamGatewayResult<SteamAppMetadataSnapshot>.Failed(
            SteamFailureKind.Transient, "http_server_error"));
        using var gateway = CreateGateway(store, source, time);

        var result = await gateway.GetAppAsync(730, string.Empty, refreshMode: SteamRefreshMode.RequireRefresh);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Name.Should().Be("Cached Name");
        result.Freshness.Should().Be(SteamFreshness.Stale);
        result.Failure.Should().Be(SteamFailureKind.Transient);
        result.DiagnosticCode.Should().Be("http_server_error");
    }

    [Test]
    public async Task Gateway_NegativeCachesOnlyNotFound()
    {
        var time = new FixedTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var store = new MemoryCacheStore();
        var source = new FakeSource(SteamGatewayResult<SteamAppMetadataSnapshot>.Failed(
            SteamFailureKind.NotFound, "store_app_not_found"));
        using var gateway = CreateGateway(store, source, time);

        var first = await gateway.GetAppAsync(999999999, string.Empty);
        var second = await gateway.GetAppAsync(999999999, string.Empty);

        first.IsSuccess.Should().BeFalse();
        second.IsSuccess.Should().BeFalse();
        second.Failure.Should().Be(SteamFailureKind.NotFound);
        second.Source.Should().Be(SteamDataSource.Sqlite);
        source.Calls.Should().Be(1);
    }

    [Test]
    public async Task Gateway_MalformedCacheIsSafeMissAndDoesNotBecomeSuccessEmpty()
    {
        var fetchedAt = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var time = new FixedTimeProvider(fetchedAt);
        var store = new MemoryCacheStore
        {
            Entry = SteamResourcePolicies.AppMetadata.CreateEntry(
                SteamCacheKey.Create("app-metadata", "public", "730"),
                "{ malformed",
                SteamDataSource.Http,
                fetchedAt)
        };
        var source = new FakeSource(SteamGatewayResult<SteamAppMetadataSnapshot>.Failed(
            SteamFailureKind.Offline, "http_connection_error"));
        using var gateway = CreateGateway(store, source, time);

        var result = await gateway.GetAppAsync(730, string.Empty);

        result.IsSuccess.Should().BeFalse();
        result.Failure.Should().Be(SteamFailureKind.Offline);
        source.Calls.Should().Be(1);
    }

    private static SteamAppCatalogGateway CreateGateway(
        ISteamResourceCacheStore store,
        ISteamAppMetadataSource source,
        TimeProvider timeProvider)
        => new(
            store,
            source,
            new SteamRequestCoalescer<SteamCacheKey>(),
            timeProvider,
            NullLogger<SteamAppCatalogGateway>.Instance);

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string body) => new(statusCode)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class FakeHttpClientFactory(HttpResponseMessage response) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new FakeHandler(response), false)
        {
            BaseAddress = name == SteamStat.Core.Http.SteamStatHttpClients.SteamStore
                ? new Uri("https://store.steampowered.com/")
                : null
        };
    }

    private sealed class FakeHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response);
    }

    private sealed class FakeSource(SteamGatewayResult<SteamAppMetadataSnapshot> result) : ISteamAppMetadataSource
    {
        public int Calls { get; private set; }

        public Task<SteamGatewayResult<SteamAppMetadataSnapshot>> GetAsync(
            uint appId,
            string language,
            string? preferredAccountName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class MemoryCacheStore : ISteamResourceCacheStore
    {
        public SteamResourceCacheEntry? Entry { get; set; }

        public Task<SteamResourceCacheEntry?> GetAsync(SteamCacheKey key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Entry?.Key == key ? Entry : null);
        }

        public Task UpsertAsync(SteamResourceCacheEntry entry, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Entry = entry;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(SteamCacheKey key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Entry?.Key == key) Entry = null;
            return Task.CompletedTask;
        }

        public Task<int> DeleteExpiredAsync(
            DateTimeOffset now,
            int batchSize,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Entry?.RetainUntil is not { } retainUntil || retainUntil > now || batchSize <= 0)
                return Task.FromResult(0);
            Entry = null;
            return Task.FromResult(1);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
