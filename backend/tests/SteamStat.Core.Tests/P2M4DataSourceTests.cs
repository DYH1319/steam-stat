using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Core.Features.Apps.Contracts;
using SteamStat.Core.Features.Profile.Contracts;
using SteamStat.Core.Sessions;
using SteamStat.Core.Steam.Cache;
using SteamStat.Core.Steam.Gateway;
using SteamStat.Core.Steam.Gateway.Internal;
using SteamStat.Core.Steam.Session;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class P2M4DataSourceTests
{
    [Test]
    public async Task AppMetadataSourceChain_TriesPicsBeforeStoreFallback()
    {
        var sessions = new EmptySessionAccessor();
        var time = new FixedTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var cm = new CmAppCatalogSource(
            sessions,
            null!,
            new SteamResultClassifier(),
            time,
            NullLogger<CmAppCatalogSource>.Instance);
        var store = new HttpStoreSource(
            new FakeHttpClientFactory(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"730\":{\"success\":true,\"data\":{\"name\":\"Counter-Strike 2\",\"type\":\"game\",\"is_free\":true}}}",
                    Encoding.UTF8,
                    "application/json")
            }),
            new SteamResultClassifier(),
            time,
            NullLogger<HttpStoreSource>.Instance);

        var result = await new SteamAppMetadataSourceChain(cm, store)
            .GetAsync(730, "english", "alice", CancellationToken.None);

        sessions.PreferredLookups.Should().Be(1);
        sessions.LoggedInLookups.Should().Be(1);
        result.IsSuccess.Should().BeTrue();
        result.Source.Should().Be(SteamDataSource.Http);
        result.Value!.Name.Should().Be("Counter-Strike 2");
    }

    [Test]
    public async Task WishlistGateway_PersistsSuccessAndReturnsStaleOnHttpFailure()
    {
        var time = new FixedTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var cache = new MemoryCacheStore();
        var source = new FakeWishlistSource(SteamGatewayResult<IReadOnlyList<uint>>.Succeeded(
            [10, 20],
            SteamDataSource.Http,
            SteamFreshness.Fresh,
            time.GetUtcNow(),
            time.GetUtcNow().AddHours(1)));
        var gateway = new SteamWishlistGateway(
            cache,
            source,
            new SteamRequestCoalescer<SteamCacheKey>(),
            time,
            NullLogger<SteamWishlistGateway>.Instance);

        var first = await gateway.GetWishlistAsync(
            76561198000000000, SteamRefreshMode.RequireRefresh);
        source.Result = SteamGatewayResult<IReadOnlyList<uint>>.Failed(
            SteamFailureKind.Offline, "http_connection_error");
        var fallback = await gateway.GetWishlistAsync(
            76561198000000000, SteamRefreshMode.RequireRefresh);

        first.Source.Should().Be(SteamDataSource.Http);
        fallback.IsSuccess.Should().BeTrue();
        fallback.Value.Should().Equal(10u, 20u);
        fallback.Source.Should().Be(SteamDataSource.Sqlite);
        fallback.Freshness.Should().Be(SteamFreshness.Stale);
        fallback.Failure.Should().Be(SteamFailureKind.Offline);
    }

    [Test]
    public async Task PresenceLocalizationGateway_ReadsPersistentCacheAfterRestartWithoutCm()
    {
        var time = new FixedTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var cache = new MemoryCacheStore();
        var successSource = new FakePresenceSource(SteamGatewayResult<IReadOnlyDictionary<string, string>>.Succeeded(
            new Dictionary<string, string> { ["#Status"] = "Playing %map%" },
            SteamDataSource.Cm,
            SteamFreshness.Fresh,
            time.GetUtcNow(),
            time.GetUtcNow().AddDays(7)));
        var first = new SteamPresenceLocalizationGateway(
            cache,
            successSource,
            new SteamRequestCoalescer<SteamCacheKey>(),
            time,
            NullLogger<SteamPresenceLocalizationGateway>.Instance);
        (await first.GetLocalizationAsync(
            "alice", 730, "english", SteamRefreshMode.RequireRefresh)).IsSuccess.Should().BeTrue();
        var unavailable = new FakePresenceSource(
            SteamGatewayResult<IReadOnlyDictionary<string, string>>.Failed(
                SteamFailureKind.Offline, "cm_unavailable"));
        var restarted = new SteamPresenceLocalizationGateway(
            cache,
            unavailable,
            new SteamRequestCoalescer<SteamCacheKey>(),
            time,
            NullLogger<SteamPresenceLocalizationGateway>.Instance);

        var cached = await restarted.GetLocalizationAsync("alice", 730, "english");

        cached.IsSuccess.Should().BeTrue();
        cached.Source.Should().Be(SteamDataSource.Sqlite);
        cached.Value!["#status"].Should().Be("Playing %map%");
        unavailable.Calls.Should().Be(0);
    }

    [Test]
    public void AvatarUriProvider_RejectsInvalidHashesAndBuildsSizedCdnUris()
    {
        var provider = new SteamAvatarUriProvider();
        const string hash = "0123456789abcdef0123456789abcdef01234567";

        provider.GetAvatarUri("invalid", SteamAvatarSize.Full).Should().BeNull();
        provider.GetAvatarUri(hash, SteamAvatarSize.Full).Should()
            .Be("https://avatars.akamai.steamstatic.com/0123456789abcdef0123456789abcdef01234567_full.jpg");
    }

    private sealed class EmptySessionAccessor : ISteamSessionAccessor
    {
        public int PreferredLookups { get; private set; }
        public int LoggedInLookups { get; private set; }

        public IReadOnlyList<string> GetLoggedInUsers()
        {
            LoggedInLookups++;
            return [];
        }

        public bool TryGetSession(string accountName, out ISteamSession session)
        {
            PreferredLookups++;
            session = null!;
            return false;
        }
    }

    private sealed class FakeWishlistSource(
        SteamGatewayResult<IReadOnlyList<uint>> result) : ISteamWishlistSource
    {
        public SteamGatewayResult<IReadOnlyList<uint>> Result { get; set; } = result;

        public Task<SteamGatewayResult<IReadOnlyList<uint>>> GetAsync(
            ulong steamId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Result);
        }
    }

    private sealed class FakePresenceSource(
        SteamGatewayResult<IReadOnlyDictionary<string, string>> result) : ISteamPresenceLocalizationSource
    {
        public int Calls { get; private set; }

        public Task<SteamGatewayResult<IReadOnlyDictionary<string, string>>> GetAsync(
            string accountName,
            uint appId,
            string language,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(result);
        }
    }

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
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(response);
    }

    private sealed class MemoryCacheStore : ISteamResourceCacheStore
    {
        private readonly Dictionary<SteamCacheKey, SteamResourceCacheEntry> _entries = [];

        public Task<SteamResourceCacheEntry?> GetAsync(
            SteamCacheKey key,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_entries.GetValueOrDefault(key));
        }

        public Task<IReadOnlyList<SteamResourceCacheEntry>> GetByResourceKindAsync(
            string resourceKind,
            int schemaVersion,
            int limit,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<SteamResourceCacheEntry> result = _entries.Values
                .Where(entry => entry.Key.ResourceKind == resourceKind && entry.Key.SchemaVersion == schemaVersion)
                .Take(limit)
                .ToArray();
            return Task.FromResult(result);
        }

        public Task UpsertAsync(
            SteamResourceCacheEntry entry,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _entries[entry.Key] = entry;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(
            SteamCacheKey key,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _entries.Remove(key);
            return Task.CompletedTask;
        }

        public Task<int> DeleteExpiredAsync(
            DateTimeOffset now,
            int batchSize,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var keys = _entries.Where(pair => pair.Value.RetainUntil <= now)
                .Take(batchSize).Select(pair => pair.Key).ToArray();
            foreach (var key in keys) _entries.Remove(key);
            return Task.FromResult(keys.Length);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
