using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Core.Events;
using SteamStat.Core.Features;
using SteamStat.Core.Features.Friends;
using SteamStat.Core.Features.Friends.Contracts;
using SteamStat.Core.Features.Library;
using SteamStat.Core.Features.Library.Contracts;
using SteamStat.Core.Steam.Cache;
using SteamStat.Core.Steam.Gateway;
using SteamStat.Core.Steam.Session;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class P3M6FeatureResultTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [Test]
    public async Task LibrarySnapshot_UsesPersistedDataWithoutCallingGateways()
    {
        var cache = new MemoryCacheStore();
        var time = new MutableTimeProvider(Now);
        var store = CreateStore(cache, time);
        await store.SaveLibraryAsync(
            "alice",
            76561198000000000,
            [new SteamOwnedGame { AppId = 730, Name = "Counter-Strike 2", IsOwned = true }],
            SteamDataSource.Cm,
            Now);
        await store.SaveFriendsAsync(
            new SteamFriendData
            {
                AccountName = "alice",
                CurrentUser = new SteamFriendInfo { SteamId = "76561198000000000", PersonaName = "Alice" },
                Friends = [new SteamFriendInfo { SteamId = "76561198000000001", PersonaName = "Bob" }],
                LastUpdateTime = checked((int)Now.ToUnixTimeSeconds())
            },
            SteamDataSource.Cm,
            Now);
        var libraryGateway = new TrackingLibraryGateway(_ => throw new InvalidOperationException("must not refresh"));
        var wishlistGateway = new TrackingWishlistGateway();
        var service = new SteamLibraryService(
            new SessionStatusProvider([]), libraryGateway, wishlistGateway,
            new StubAppNameResolver(), new StubAppMetadataWriter(), store, time,
            NullLogger<SteamLibraryService>.Instance);

        var result = await service.GetLibrarySnapshotAsync();

        result.Status.Should().Be(SteamFeatureResultStatus.Success);
        result.Libraries.Should().ContainKey("alice")
            .WhoseValue.Should().ContainSingle(game => game.AppId == 730);
        result.Resources.Should().OnlyContain(status =>
            status.ResourceKind == SteamFeatureSnapshotStore.LibraryResourceKind);
        libraryGateway.Calls.Should().Be(0);
        wishlistGateway.Calls.Should().Be(0);
    }

    [Test]
    public async Task LibraryRefresh_RunsOneRefreshPassAndReportsResources()
    {
        var cache = new MemoryCacheStore();
        var time = new MutableTimeProvider(Now);
        var store = CreateStore(cache, time);
        var libraryGateway = new TrackingLibraryGateway(accountName => SteamGatewayResult<SteamLibrarySnapshot>.Succeeded(
            new SteamLibrarySnapshot(
                76561198000000000,
                1,
                [new SteamLibraryGameSnapshot(
                    440, "Team Fortress 2", "Team Fortress 2", 500, 0, 0, string.Empty,
                    true, [], true, false, [], [], 0, 0, 0)],
                [],
                new Dictionary<uint, IReadOnlyList<string>>()),
            SteamDataSource.Cm,
            SteamFreshness.Fresh,
            time.GetUtcNow(),
            time.GetUtcNow()));
        var wishlistGateway = new TrackingWishlistGateway();
        var service = new SteamLibraryService(
            new SessionStatusProvider([new SteamSessionStatusSnapshot("alice", SteamSessionState.Ready, 1, 0)]),
            libraryGateway, wishlistGateway,
            new StubAppNameResolver(), new StubAppMetadataWriter(), store, time,
            NullLogger<SteamLibraryService>.Instance);

        var result = await service.RefreshLibraryAsync();

        libraryGateway.Calls.Should().Be(1);
        result.Status.Should().Be(SteamFeatureResultStatus.Success);
        result.Libraries.Should().ContainKey("alice")
            .WhoseValue.Should().ContainSingle(game => game.AppId == 440 && game.IsOwned);
        result.Resources.Should().OnlyContain(status =>
            status.ResourceKind == SteamFeatureSnapshotStore.LibraryResourceKind
            && status.Freshness == SteamFreshness.Fresh
            && status.Failure == null);
    }

    [Test]
    public async Task LibraryRefresh_ClassifiesFailureWhenNoDataSurvives()
    {
        var cache = new MemoryCacheStore();
        var time = new MutableTimeProvider(Now);
        var store = CreateStore(cache, time);
        var libraryGateway = new TrackingLibraryGateway(_ =>
            SteamGatewayResult<SteamLibrarySnapshot>.Failed(SteamFailureKind.Offline, "library_offline"));
        var service = new SteamLibraryService(
            new SessionStatusProvider([new SteamSessionStatusSnapshot("alice", SteamSessionState.Ready, 1, 0)]),
            libraryGateway, new TrackingWishlistGateway(),
            new StubAppNameResolver(), new StubAppMetadataWriter(), store, time,
            NullLogger<SteamLibraryService>.Instance);

        var result = await service.RefreshLibraryAsync();

        libraryGateway.Calls.Should().Be(1);
        result.Status.Should().Be(SteamFeatureResultStatus.Failure);
        result.Libraries.Should().BeEmpty();
        result.Resources.Should().Contain(status =>
            status.ResourceKind == SteamFeatureSnapshotStore.LibraryResourceKind
            && status.AccountName == "alice"
            && status.Failure != null
            && status.DiagnosticCode != null);
    }

    [Test]
    public async Task LibraryRefresh_ClassifiesPartialForStaleEmptySnapshot()
    {
        var cache = new MemoryCacheStore();
        var time = new MutableTimeProvider(Now);
        var store = CreateStore(cache, time);
        await store.SaveLibraryAsync(
            "alice",
            76561198000000000,
            [],
            SteamDataSource.Cm,
            Now.AddDays(-1));
        var libraryGateway = new TrackingLibraryGateway(_ =>
            SteamGatewayResult<SteamLibrarySnapshot>.Failed(SteamFailureKind.Offline, "library_offline"));
        var service = new SteamLibraryService(
            new SessionStatusProvider([new SteamSessionStatusSnapshot("alice", SteamSessionState.Ready, 1, 0)]),
            libraryGateway, new TrackingWishlistGateway(),
            new StubAppNameResolver(), new StubAppMetadataWriter(), store, time,
            NullLogger<SteamLibraryService>.Instance);

        var result = await service.RefreshLibraryAsync();

        libraryGateway.Calls.Should().Be(1);
        result.Status.Should().Be(SteamFeatureResultStatus.Partial);
        result.Libraries.Should().ContainKey("alice").WhoseValue.Should().BeEmpty();
        result.Resources.Should().Contain(status =>
            status.AccountName == "alice"
            && status.Freshness == SteamFreshness.Stale
            && status.Failure == SteamFailureKind.Offline);
    }

    [Test]
    public async Task LibraryRefresh_ClassifiesPartialWhenDataSurvivesAlongsideFailures()
    {
        var cache = new MemoryCacheStore();
        var time = new MutableTimeProvider(Now);
        var store = CreateStore(cache, time);
        await store.SaveLibraryAsync(
            "bob",
            76561198000000001,
            [new SteamOwnedGame { AppId = 730, Name = "Counter-Strike 2", IsOwned = true }],
            SteamDataSource.Cm,
            Now.AddDays(-1));
        var libraryGateway = new TrackingLibraryGateway(_ =>
            SteamGatewayResult<SteamLibrarySnapshot>.Failed(SteamFailureKind.Offline, "library_offline"));
        var service = new SteamLibraryService(
            new SessionStatusProvider([
                new SteamSessionStatusSnapshot("alice", SteamSessionState.Ready, 1, 0),
                new SteamSessionStatusSnapshot("bob", SteamSessionState.ReauthenticationRequired, 2, 0)
            ]),
            libraryGateway, new TrackingWishlistGateway(),
            new StubAppNameResolver(), new StubAppMetadataWriter(), store, time,
            NullLogger<SteamLibraryService>.Instance);

        var result = await service.RefreshLibraryAsync();

        libraryGateway.Calls.Should().Be(1);
        result.Status.Should().Be(SteamFeatureResultStatus.Partial);
        result.Libraries["bob"].Should().ContainSingle(game => game.AppId == 730);
        result.Resources.Should().Contain(status =>
            status.AccountName == "bob" && status.Failure == SteamFailureKind.AuthenticationRequired);
        result.Resources.Should().Contain(status =>
            status.AccountName == "alice" && status.Failure != null);
    }

    [Test]
    public async Task FriendsSnapshot_UsesCachedDataWithoutTouchingPresenceFeed()
    {
        var cache = new MemoryCacheStore();
        var time = new MutableTimeProvider(Now);
        var store = CreateStore(cache, time);
        await store.SaveFriendsAsync(
            new SteamFriendData
            {
                AccountName = "alice",
                CurrentUser = new SteamFriendInfo { SteamId = "76561198000000000", PersonaName = "Alice" },
                Friends = [new SteamFriendInfo { SteamId = "76561198000000001", PersonaName = "Bob" }],
                LastUpdateTime = checked((int)Now.ToUnixTimeSeconds())
            },
            SteamDataSource.Cm,
            Now);
        await store.SaveLibraryAsync(
            "alice",
            76561198000000000,
            [new SteamOwnedGame { AppId = 730, Name = "Counter-Strike 2", IsOwned = true }],
            SteamDataSource.Cm,
            Now);
        var feed = new TrackingPresenceFeed(["alice"]);
        await using var service = new SteamFriendsService(
            feed, new StubAppNameResolver(), null!, null!, new NullEventBus(), store, time,
            NullLogger<SteamFriendsService>.Instance);

        var result = await service.GetFriendsSnapshotAsync();

        result.Status.Should().Be(SteamFeatureResultStatus.Success);
        result.Accounts.Should().ContainSingle(data =>
            data.AccountName == "alice" && data.Friends.Count == 1);
        result.Resources.Should().OnlyContain(status =>
            status.ResourceKind == SteamFeatureSnapshotStore.FriendsResourceKind);
        feed.SnapshotCalls.Should().Be(0);
        feed.LoggedInCalls.Should().Be(0);
    }

    [Test]
    public async Task FriendsRefresh_RunsOneRefreshPassAndReportsResources()
    {
        var cache = new MemoryCacheStore();
        var time = new MutableTimeProvider(Now);
        var store = CreateStore(cache, time);
        var feed = new TrackingPresenceFeed(["alice"]);
        await using var service = new SteamFriendsService(
            feed, new StubAppNameResolver(), null!, null!, new NullEventBus(), store, time,
            NullLogger<SteamFriendsService>.Instance);

        var result = await service.RefreshFriendsAsync();

        feed.SnapshotCalls.Should().Be(1);
        feed.LoggedInCalls.Should().Be(1);
        result.Status.Should().Be(SteamFeatureResultStatus.Success);
        result.Accounts.Should().ContainSingle(data =>
            data.AccountName == "alice"
            && data.CurrentUser.PersonaName == "Alice"
            && data.Friends.Single().PersonaName == "Bob");
        result.Resources.Should().OnlyContain(status =>
            status.ResourceKind == SteamFeatureSnapshotStore.FriendsResourceKind
            && status.Freshness == SteamFreshness.Fresh
            && status.Failure == null);
    }

    [Test]
    public async Task FriendsResult_ClassifiesEmptySuccessFailureAndPartial()
    {
        var cache = new MemoryCacheStore();
        var time = new MutableTimeProvider(Now);
        var store = CreateStore(cache, time);
        var feed = new TrackingPresenceFeed([]);
        await using var service = new SteamFriendsService(
            feed, new StubAppNameResolver(), null!, null!, new NullEventBus(), store, time,
            NullLogger<SteamFriendsService>.Instance);

        var empty = await service.GetFriendsSnapshotAsync();
        empty.Status.Should().Be(SteamFeatureResultStatus.Success);
        empty.Accounts.Should().BeEmpty();

        store.ReportFailure(
            SteamFeatureSnapshotStore.FriendsResourceKind,
            "alice",
            SteamFailureKind.Offline,
            "friends_session_unavailable");
        var failure = await service.GetFriendsSnapshotAsync();
        failure.Status.Should().Be(SteamFeatureResultStatus.Failure);
        failure.Resources.Should().Contain(status => status.DiagnosticCode == "friends_session_unavailable");

        await store.SaveFriendsAsync(
            new SteamFriendData
            {
                AccountName = "alice",
                CurrentUser = new SteamFriendInfo { SteamId = "76561198000000000", PersonaName = "Alice" },
                Friends = [],
                LastUpdateTime = checked((int)Now.ToUnixTimeSeconds())
            },
            SteamDataSource.Cm,
            Now);
        store.ReportFailure(
            SteamFeatureSnapshotStore.FriendsResourceKind,
            "bob",
            SteamFailureKind.Offline,
            "friends_session_unavailable");
        var partial = await service.GetFriendsSnapshotAsync();
        partial.Status.Should().Be(SteamFeatureResultStatus.Partial);
        partial.Accounts.Should().ContainSingle(data => data.AccountName == "alice");
    }

    private static SteamFeatureSnapshotStore CreateStore(ISteamResourceCacheStore cache, TimeProvider time)
        => new(cache, time, NullLogger<SteamFeatureSnapshotStore>.Instance);

    private sealed class SessionStatusProvider(IReadOnlyList<SteamSessionStatusSnapshot> statuses)
        : ISteamSessionStatusProvider
    {
        public IReadOnlyList<SteamSessionStatusSnapshot> GetSessionStatuses() => statuses;
    }

    private sealed class TrackingLibraryGateway(Func<string, SteamGatewayResult<SteamLibrarySnapshot>> responder)
        : ISteamLibraryGateway
    {
        public int Calls { get; private set; }

        public Task<SteamGatewayResult<SteamLibrarySnapshot>> GetLibraryAsync(
            string accountName,
            bool includeFamilyShared,
            CancellationToken cancellationToken = default)
        {
            Calls += 1;
            return Task.FromResult(responder(accountName));
        }
    }

    private sealed class TrackingWishlistGateway : ISteamWishlistGateway
    {
        public int Calls { get; private set; }

        public Task<SteamGatewayResult<IReadOnlyList<uint>>> GetWishlistAsync(
            ulong steamId,
            SteamRefreshMode refreshMode = SteamRefreshMode.PreferCache,
            CancellationToken cancellationToken = default)
        {
            Calls += 1;
            return Task.FromResult(SteamGatewayResult<IReadOnlyList<uint>>.Succeeded(
                [], SteamDataSource.Http, SteamFreshness.Fresh, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }
    }

    private sealed class TrackingPresenceFeed(IReadOnlyList<string> accounts) : ISteamPresenceFeed
    {
        public int SnapshotCalls { get; private set; }
        public int LoggedInCalls { get; private set; }

        public IReadOnlyList<string> GetLoggedInAccounts()
        {
            LoggedInCalls += 1;
            return accounts;
        }

        public bool TryGetSnapshot(
            string accountName,
            out SteamPersonaSnapshot currentUser,
            out IReadOnlyList<SteamPersonaSnapshot> friends)
        {
            SnapshotCalls += 1;
            if (!accounts.Contains(accountName, StringComparer.OrdinalIgnoreCase))
            {
                currentUser = null!;
                friends = [];
                return false;
            }
            currentUser = new SteamPersonaSnapshot(76561198000000000, "Alice", 1, 0, 0, string.Empty);
            friends = [new SteamPersonaSnapshot(76561198000000001, "Bob", 1, 3, 0, string.Empty)];
            return true;
        }

        public IDisposable? Subscribe(string accountName, SteamPresenceFeedHandlers handlers) => null;
        public void RequestRichPresence(string accountName, uint appId, IReadOnlyList<ulong> steamIds) { }
        public void RequestLevels(string accountName, IReadOnlyList<ulong> steamIds) { }
        public void RequestFriendInfo(string accountName, ulong steamId) { }
    }

    private sealed class StubAppNameResolver : IAppNameResolver
    {
        public string? GetCachedName(uint appId) => $"App {appId}";
        public Task<string?> ResolveNameAsync(uint appId, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class StubAppMetadataWriter : IAppMetadataWriter
    {
        public Task EnsureCachedAsync(IEnumerable<AppMetadata> apps, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class NullEventBus : IEventBus
    {
        public Task PublishAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default)
            where TEvent : notnull
            => Task.CompletedTask;
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

        public Task UpsertAsync(SteamResourceCacheEntry entry, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _entries[entry.Key] = entry;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(SteamCacheKey key, CancellationToken cancellationToken = default)
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
            return Task.FromResult(0);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
