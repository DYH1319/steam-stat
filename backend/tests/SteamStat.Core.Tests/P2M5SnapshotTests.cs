using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Core.Features.Friends;
using SteamStat.Core.Features.Friends.Contracts;
using SteamStat.Core.Features.Library;
using SteamStat.Core.Http;
using SteamStat.Core.Steam;
using SteamStat.Core.Steam.Cache;
using SteamStat.Core.Steam.Gateway;
using SteamStat.Core.Steam.Session;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class P2M5SnapshotTests
{
    [Test]
    public async Task LibraryAndFriendsSnapshots_RoundTripAfterRestartAndBecomeStale()
    {
        var cache = new MemoryCacheStore();
        var time = new MutableTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var writer = CreateStore(cache, time);
        await writer.SaveLibraryAsync(
            "alice",
            76561198000000000,
            [new SteamOwnedGame { AppId = 730, Name = "Counter-Strike 2", IsOwned = true }],
            SteamDataSource.Cm,
            time.GetUtcNow());
        await writer.SaveFriendsAsync(
            new SteamFriendData
            {
                AccountName = "alice",
                CurrentUser = new SteamFriendInfo { SteamId = "76561198000000000", PersonaName = "Alice" },
                Friends = [new SteamFriendInfo { SteamId = "76561198000000001", PersonaName = "Bob" }],
                LastUpdateTime = checked((int)time.GetUtcNow().ToUnixTimeSeconds())
            },
            SteamDataSource.Cm,
            time.GetUtcNow());
        time.Advance(TimeSpan.FromHours(1));
        var restarted = CreateStore(cache, time);

        var library = await restarted.GetLibraryAsync("alice");
        var friends = await restarted.GetFriendsAsync("alice");
        var statuses = await restarted.GetResourceStatusesAsync();

        library.Should().NotBeNull();
        library!.Freshness.Should().Be(SteamFreshness.Stale);
        library.Value.Should().ContainSingle(game => game.AppId == 730);
        friends.Should().NotBeNull();
        friends!.Freshness.Should().Be(SteamFreshness.Stale);
        friends.Value.Friends.Should().ContainSingle(friend => friend.PersonaName == "Bob");
        statuses.Should().HaveCount(2)
            .And.OnlyContain(status => status.Source == SteamDataSource.Sqlite
                && status.LastSuccessfulUpdate == DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
    }

    [Test]
    public async Task FeatureServices_ReturnPersistedSnapshotsWithoutReadySessions()
    {
        var cache = new MemoryCacheStore();
        var time = new MutableTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var writer = CreateStore(cache, time);
        await writer.SaveLibraryAsync(
            "alice",
            76561198000000000,
            [new SteamOwnedGame { AppId = 730, Name = "Counter-Strike 2", IsOwned = true }],
            SteamDataSource.Cm,
            time.GetUtcNow());
        await writer.SaveFriendsAsync(
            new SteamFriendData
            {
                AccountName = "alice",
                CurrentUser = new SteamFriendInfo { SteamId = "76561198000000000", PersonaName = "Alice" },
                Friends = [new SteamFriendInfo { SteamId = "76561198000000001", PersonaName = "Bob" }],
                LastUpdateTime = checked((int)time.GetUtcNow().ToUnixTimeSeconds())
            },
            SteamDataSource.Cm,
            time.GetUtcNow());
        var restarted = CreateStore(cache, time);
        var libraryService = new SteamLibraryService(
            new SessionStatusProvider([]), null!, null!, null!, null!, restarted, time,
            NullLogger<SteamLibraryService>.Instance);
        await using var friendsService = new SteamFriendsService(
            new EmptyPresenceFeed(), null!, null!, null!, null!, restarted, time,
            NullLogger<SteamFriendsService>.Instance);

        var libraries = await libraryService.GetLibraryForAllUsersAsync();
        var friends = await friendsService.GetAllLoggedInUsersFriendsAsync();
        var statuses = await restarted.GetResourceStatusesAsync();

        libraries["alice"].Should().ContainSingle(game => game.AppId == 730);
        friends.Should().ContainSingle(data => data.AccountName == "alice" && data.Friends.Count == 1);
        statuses.Should().OnlyContain(status => status.Source == SteamDataSource.Sqlite
            && status.Freshness == SteamFreshness.Stale
            && status.Failure == SteamFailureKind.AuthenticationRequired);
    }

    [Test]
    public async Task OperationalStatus_ReportsOfflineFailureAndReauthenticationWithoutSecrets()
    {
        var cache = new MemoryCacheStore();
        var time = new MutableTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var snapshots = CreateStore(cache, time);
        snapshots.ReportFailure(
            SteamFeatureSnapshotStore.LibraryResourceKind,
            "alice",
            SteamFailureKind.Offline,
            "library_session_unavailable");
        var sessions = new SessionStatusProvider([
            new SteamSessionStatusSnapshot(
                "alice", SteamSessionState.ReauthenticationRequired, 3, 1, "Expired")
        ]);
        var service = new SteamOperationalStatusService(
            new SteamConnectivityMonitor(new SteamAccessOptions(), time),
            sessions,
            snapshots);

        var status = await service.GetAsync();

        status.Connectivity.Should().Be(SteamConnectivitySummary.Offline);
        status.ReauthenticationAccounts.Should().Equal("alice");
        status.Resources.Should().ContainSingle(resource => resource.AccountName == "alice"
            && resource.Failure == SteamFailureKind.Offline
            && resource.Source == null
            && resource.LastSuccessfulUpdate == null);
    }

    [Test]
    public async Task FailedWrite_DoesNotReplaceLastSuccessfulSnapshot()
    {
        var cache = new MemoryCacheStore();
        var time = new MutableTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var store = CreateStore(cache, time);
        await store.SaveLibraryAsync(
            "alice",
            76561198000000000,
            [new SteamOwnedGame { AppId = 10, Name = "Old", IsOwned = true }],
            SteamDataSource.Cm,
            time.GetUtcNow());
        cache.RejectWrites = true;

        var action = () => store.SaveLibraryAsync(
            "alice",
            76561198000000000,
            [new SteamOwnedGame { AppId = 20, Name = "New", IsOwned = true }],
            SteamDataSource.Cm,
            time.GetUtcNow().AddMinutes(1));

        await action.Should().ThrowAsync<InvalidOperationException>();
        (await CreateStore(cache, time).GetLibraryAsync("alice"))!.Value
            .Should().ContainSingle(game => game.AppId == 10);
    }

    private static SteamFeatureSnapshotStore CreateStore(ISteamResourceCacheStore cache, TimeProvider time)
        => new(cache, time, NullLogger<SteamFeatureSnapshotStore>.Instance);

    private sealed class SessionStatusProvider(IReadOnlyList<SteamSessionStatusSnapshot> statuses)
        : ISteamSessionStatusProvider
    {
        public IReadOnlyList<SteamSessionStatusSnapshot> GetSessionStatuses() => statuses;
    }

    private sealed class EmptyPresenceFeed : ISteamPresenceFeed
    {
        public IReadOnlyList<string> GetLoggedInAccounts() => [];

        public bool TryGetSnapshot(
            string accountName,
            out SteamPersonaSnapshot currentUser,
            out IReadOnlyList<SteamPersonaSnapshot> friends)
        {
            currentUser = null!;
            friends = [];
            return false;
        }

        public IDisposable? Subscribe(string accountName, SteamPresenceFeedHandlers handlers) => null;
        public void RequestRichPresence(string accountName, uint appId, IReadOnlyList<ulong> steamIds) { }
        public void RequestLevels(string accountName, IReadOnlyList<ulong> steamIds) { }
        public void RequestFriendInfo(string accountName, ulong steamId) { }
    }

    private sealed class MemoryCacheStore : ISteamResourceCacheStore
    {
        private readonly Dictionary<SteamCacheKey, SteamResourceCacheEntry> _entries = [];
        public bool RejectWrites { get; set; }

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
            if (RejectWrites) throw new InvalidOperationException("Rejected test write.");
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
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
