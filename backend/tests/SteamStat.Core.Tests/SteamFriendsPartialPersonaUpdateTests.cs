using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Core.Events;
using SteamStat.Core.Features;
using SteamStat.Core.Features.Friends;
using SteamStat.Core.Features.Friends.Contracts;
using SteamStat.Core.Steam.Cache;

namespace SteamStat.Core.Tests;

/// <summary>
/// Steam 常只推送部分好友状态字段（由 status_flags 标明）；未携带的字段不能当作已清空。
/// </summary>
[TestFixture]
public sealed class SteamFriendsPartialPersonaUpdateTests
{
    private const ulong Me = 76561198000000000;
    private const ulong Bob = 76561198000000001;
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [Test]
    public async Task PartialPersonaUpdate_KeepsFieldsItDoesNotCarry()
    {
        var (service, feed) = await CreateAsync();
        await using var _ = service;

        // 只携带 LastSeen：GameAppId / 昵称是默认值，不应清空游戏与昵称
        feed.Handlers!.PersonaChanged(new SteamPersonaUpdate(
            Bob, string.Empty, 0, 0, 0, string.Empty, 100, 200, SteamPersonaUpdateFields.LastSeen));

        var bob = await GetBobAsync(service);
        bob.GameId.Should().Be("730");
        bob.GameName.Should().Be("Counter-Strike 2");
        bob.PersonaName.Should().Be("Bob");
        bob.PersonaState.Should().Be(1);
        bob.LastLogOff.Should().Be(100);
        bob.LastLogOn.Should().Be(200);
    }

    [Test]
    public async Task GameInfoUpdate_ClearsGameWhenFriendStopsPlaying()
    {
        var (service, feed) = await CreateAsync();
        await using var _ = service;

        feed.Handlers!.PersonaChanged(new SteamPersonaUpdate(
            Bob, string.Empty, 0, 0, 0, string.Empty, 0, 0, SteamPersonaUpdateFields.GameInfo));

        var bob = await GetBobAsync(service);
        bob.GameId.Should().Be("0");
        bob.GameName.Should().BeEmpty();
        bob.PersonaName.Should().Be("Bob");
    }

    [Test]
    public async Task FullPersonaUpdate_AppliesAllFields()
    {
        var (service, feed) = await CreateAsync();
        await using var _ = service;

        feed.Handlers!.PersonaChanged(new SteamPersonaUpdate(Bob, "Bobby", 3, 0, 0, string.Empty, 0, 0));

        var bob = await GetBobAsync(service);
        bob.PersonaName.Should().Be("Bobby");
        bob.PersonaState.Should().Be(3);
        bob.GameId.Should().Be("0");
    }

    [Test]
    public async Task Nicknames_AreAppliedWhenFriendsLoad()
    {
        var (service, feed) = await CreateAsync();
        await using var _ = service;

        feed.Nicknames = new Dictionary<ulong, string> { [Bob] = "Old friend" };
        await service.RefreshFriendsAsync();

        var bob = await GetBobAsync(service);
        bob.Nickname.Should().Be("Old friend");
        bob.PersonaName.Should().Be("Bob");
    }

    [Test]
    public async Task Nicknames_SurviveAFailedRead()
    {
        var (service, feed) = await CreateAsync();
        await using var _ = service;
        feed.Nicknames = new Dictionary<ulong, string> { [Bob] = "Old friend" };
        await service.RefreshFriendsAsync();

        feed.Nicknames = null;
        await service.RefreshFriendsAsync();

        (await GetBobAsync(service)).Nickname.Should().Be("Old friend");
    }

    [Test]
    public async Task NicknamesChanged_RereadsTheList()
    {
        var (service, feed) = await CreateAsync();
        await using var _ = service;

        feed.Nicknames = new Dictionary<ulong, string> { [Bob] = "Renamed" };
        feed.Handlers!.NicknamesChanged();
        (await GetBobAsync(service)).Nickname.Should().Be("Renamed");

        feed.Nicknames = new Dictionary<ulong, string>();
        feed.Handlers!.NicknamesChanged();
        (await GetBobAsync(service)).Nickname.Should().BeEmpty();
    }

    private static async Task<(SteamFriendsService Service, CapturingPresenceFeed Feed)> CreateAsync()
    {
        var time = new FixedTimeProvider(Now);
        var store = new SteamFeatureSnapshotStore(new MemoryCacheStore(), time, NullLogger<SteamFeatureSnapshotStore>.Instance);
        var feed = new CapturingPresenceFeed();
        var service = new SteamFriendsService(
            feed, new StubAppNameResolver(), null!, new UntrackedRecorder(), new NullEventBus(), store, time,
            NullLogger<SteamFriendsService>.Instance);
        await service.RefreshFriendsAsync();
        feed.Handlers.Should().NotBeNull();
        return (service, feed);
    }

    private static async Task<SteamFriendInfo> GetBobAsync(SteamFriendsService service)
    {
        var result = await service.GetFriendsSnapshotAsync();
        return result.Accounts.Single().Friends.Single(friend => friend.SteamId == Bob.ToString());
    }

    private sealed class CapturingPresenceFeed : ISteamPresenceFeed
    {
        public SteamPresenceFeedHandlers? Handlers { get; private set; }
        public IReadOnlyDictionary<ulong, string>? Nicknames { get; set; } = new Dictionary<ulong, string>();

        public IReadOnlyList<string> GetLoggedInAccounts() => ["alice"];

        public bool TryGetSnapshot(
            string accountName,
            out SteamPersonaSnapshot currentUser,
            out IReadOnlyList<SteamPersonaSnapshot> friends)
        {
            currentUser = new SteamPersonaSnapshot(Me, "Alice", 1, 0, 0, string.Empty);
            friends = [new SteamPersonaSnapshot(Bob, "Bob", 1, 3, 730, string.Empty)];
            return true;
        }

        public IDisposable? Subscribe(string accountName, SteamPresenceFeedHandlers handlers)
        {
            Handlers = handlers;
            return new NoopDisposable();
        }

        public void RequestRichPresence(string accountName, uint appId, IReadOnlyList<ulong> steamIds) { }
        public void RequestLevels(string accountName, IReadOnlyList<ulong> steamIds) { }
        public void RequestFriendInfo(string accountName, ulong steamId) { }
        public Task<IReadOnlyDictionary<ulong, string>?> GetNicknamesAsync(
            string accountName, CancellationToken cancellationToken = default)
            => Task.FromResult(Nicknames);
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class StubAppNameResolver : IAppNameResolver
    {
        public string? GetCachedName(uint appId) => appId == 730 ? "Counter-Strike 2" : null;
        public Task<string?> ResolveNameAsync(uint appId, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class UntrackedRecorder : IFriendStatusRecorder
    {
        public bool IsTracked(string accountName, string friendSteamId) => false;

        public Task RecordChangeAsync(
            string accountName,
            string friendSteamId,
            string friendPersonaName,
            string changeType,
            FriendStatusValue? previousValue,
            FriendStatusValue? currentValue,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
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

        public Task<SteamResourceCacheEntry?> GetAsync(SteamCacheKey key, CancellationToken cancellationToken = default)
        {
            lock (_entries) return Task.FromResult(_entries.GetValueOrDefault(key));
        }

        public Task<IReadOnlyList<SteamResourceCacheEntry>> GetByResourceKindAsync(
            string resourceKind,
            int schemaVersion,
            int limit,
            CancellationToken cancellationToken = default)
        {
            lock (_entries)
            {
                IReadOnlyList<SteamResourceCacheEntry> result = _entries.Values
                    .Where(entry => entry.Key.ResourceKind == resourceKind && entry.Key.SchemaVersion == schemaVersion)
                    .Take(limit)
                    .ToArray();
                return Task.FromResult(result);
            }
        }

        public Task UpsertAsync(SteamResourceCacheEntry entry, CancellationToken cancellationToken = default)
        {
            lock (_entries) _entries[entry.Key] = entry;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(SteamCacheKey key, CancellationToken cancellationToken = default)
        {
            lock (_entries) _entries.Remove(key);
            return Task.CompletedTask;
        }

        public Task<int> DeleteExpiredAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken = default)
            => Task.FromResult(0);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
