using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Core.Events;
using SteamStat.Core.Features;
using SteamStat.Core.Features.Friends;
using SteamStat.Core.Features.Friends.Contracts;
using SteamStat.Core.Steam.Cache;

namespace SteamStat.Core.Tests;

/// <summary>
/// 登录就绪和好友列表回调会同时触发拉取。好友列表还没下发时的空结果不能覆盖已有数据，
/// 会话重连后没变的好友状态也不能再记一遍。
/// </summary>
[TestFixture]
public sealed class SteamFriendsListRaceTests
{
    private const string Account = "alice";
    private const ulong Me = 76561198000000000;
    private const ulong Bob = 76561198000000001;
    private const ulong Carol = 76561198000000002;
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    private static readonly SteamPersonaSnapshot BobOnline = new(Bob, "Bob", 1, 3, 730, string.Empty);
    private static readonly SteamPersonaSnapshot CarolOnline = new(Carol, "Carol", 1, 3, 0, string.Empty);

    [Test]
    public async Task EmptyListBeforeTheCallback_KeepsTheOfflineSnapshot()
    {
        var context = Create();
        await using var _ = context.Service;
        context.Feed.Friends = [BobOnline];
        await context.Service.HandleAsync(new SteamSessionReady(Account), CancellationToken.None);
        context.Feed.Handlers!.FriendsListChanged();
        await context.Service.HandleAsync(new SteamSessionEnded(Account), CancellationToken.None);

        // 重新登录：会话就绪了，好友列表还没下发
        context.Feed.Friends = [];
        await context.Service.HandleAsync(new SteamSessionReady(Account), CancellationToken.None);

        (await FriendIdsAsync(context.Service)).Should().Equal(Bob.ToString());
        (await context.Store.GetFriendsAsync(Account))!.Value.Friends.Should().ContainSingle();

        context.Feed.Friends = [BobOnline, CarolOnline];
        context.Feed.Handlers!.FriendsListChanged();
        await WaitForAsync(async () => (await FriendIdsAsync(context.Service)).Count == 2);
    }

    [Test]
    public async Task EmptyList_IsAcceptedOnceTheGracePeriodPasses()
    {
        var context = Create();
        await using var _ = context.Service;
        context.Feed.Friends = [BobOnline];
        await context.Service.HandleAsync(new SteamSessionReady(Account), CancellationToken.None);
        context.Feed.Handlers!.FriendsListChanged();
        await context.Service.HandleAsync(new SteamSessionEnded(Account), CancellationToken.None);

        // 好友真的删光了：回调不会来，宽限期过后接受空列表
        context.Service.EmptyListGrace = TimeSpan.FromMilliseconds(30);
        context.Feed.Friends = [];
        await context.Service.HandleAsync(new SteamSessionReady(Account), CancellationToken.None);

        await WaitForAsync(async () => (await FriendIdsAsync(context.Service)).Count == 0);
    }

    [Test]
    public async Task OverlappingFetches_EndWithTheNewestList()
    {
        var context = Create();
        await using var _ = context.Service;
        context.Feed.Friends = [BobOnline];
        await context.Service.HandleAsync(new SteamSessionReady(Account), CancellationToken.None);

        // 第一次拉取卡在昵称接口上时列表变了。不串行的话，先取的旧快照会在后面覆盖新的
        var nicknames = new TaskCompletionSource<IReadOnlyDictionary<ulong, string>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Feed.PendingNicknames = nicknames;
        var first = context.Service.RefreshFriendsAsync();
        await WaitForAsync(() => Task.FromResult(context.Feed.NicknameCalls >= 2));
        context.Feed.Friends = [BobOnline, CarolOnline];
        context.Feed.PendingNicknames = null;
        context.Feed.Handlers!.FriendsListChanged();
        await Task.Delay(30);
        nicknames.SetResult(new Dictionary<ulong, string>());
        await first;

        await WaitForAsync(async () => (await FriendIdsAsync(context.Service)).Count == 2);
        await Task.Delay(30);
        (await FriendIdsAsync(context.Service)).Should().HaveCount(2);
    }

    [Test]
    public async Task Reconnect_ReportsTrackedFriendsForReconciliationInsteadOfAsNewChanges()
    {
        var context = Create(tracked: true);
        await using var _ = context.Service;
        // 资料还没到：名字是空的，状态是默认的离线
        context.Feed.Friends = [new SteamPersonaSnapshot(Bob, string.Empty, 0, 3, 0, string.Empty)];
        await context.Service.HandleAsync(new SteamSessionReady(Account), CancellationToken.None);
        context.Feed.Handlers!.FriendsListChanged();
        await WaitForAsync(async () => (await FriendIdsAsync(context.Service)).Count == 1);
        context.Recorder.Calls.Should().BeEmpty("没有资料的好友不能当成离线去对账");

        context.Feed.Handlers!.PersonaChanged(new SteamPersonaUpdate(Bob, "Bob", 1, 0, 730, string.Empty, 0, 0));
        context.Recorder.Calls.Select(call => call.ChangeType).Should().BeEquivalentTo("state", "game");

        // 同一次会话里没变的推送不再上报
        context.Recorder.Calls.Clear();
        context.Feed.Handlers!.PersonaChanged(new SteamPersonaUpdate(Bob, "Bob", 1, 0, 730, string.Empty, 0, 0));
        context.Recorder.Calls.Should().BeEmpty();

        // 断线重连后好友还在线、还在玩同一款：仍然上报一次，由记录器和落库的最后一条比较后丢弃
        await context.Service.HandleAsync(new SteamSessionEnded(Account), CancellationToken.None);
        context.Recorder.Calls.Clear();
        context.Feed.Friends = [new SteamPersonaSnapshot(Bob, string.Empty, 0, 3, 0, string.Empty)];
        await context.Service.HandleAsync(new SteamSessionReady(Account), CancellationToken.None);
        context.Feed.Handlers!.FriendsListChanged();
        await WaitForAsync(async () => (await FriendIdsAsync(context.Service)).Count == 1);
        context.Feed.Handlers!.PersonaChanged(new SteamPersonaUpdate(Bob, "Bob", 1, 0, 730, string.Empty, 0, 0));
        context.Recorder.Calls.Select(call => (call.ChangeType, call.Current)).Should().BeEquivalentTo(new[]
        {
            ("state", new FriendStatusValue(PersonaState: 1)),
            ("game", new FriendStatusValue(GameId: "730", GameName: "Counter-Strike 2"))
        });
    }

    [Test]
    public async Task FriendsWhoseStateArrivedBeforeTheFetch_AreReconciledAtFetchTime()
    {
        var context = Create(tracked: true);
        await using var _ = context.Service;
        context.Feed.Friends = [BobOnline];

        await context.Service.HandleAsync(new SteamSessionReady(Account), CancellationToken.None);

        context.Recorder.Calls.Select(call => call.ChangeType).Should().BeEquivalentTo("state", "game");
    }

    private static async Task<IReadOnlyList<string>> FriendIdsAsync(SteamFriendsService service)
        => (await service.GetFriendsSnapshotAsync()).Accounts.SingleOrDefault()?.Friends.Select(friend => friend.SteamId).ToArray() ?? [];

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 400 && !await condition(); attempt++) await Task.Delay(5);
        (await condition()).Should().BeTrue();
    }

    private static (SteamFriendsService Service, ScriptedPresenceFeed Feed, SteamFeatureSnapshotStore Store, CapturingRecorder Recorder) Create(bool tracked = false)
    {
        var time = new FixedTimeProvider(Now);
        var store = new SteamFeatureSnapshotStore(new MemoryCacheStore(), time, NullLogger<SteamFeatureSnapshotStore>.Instance);
        var feed = new ScriptedPresenceFeed();
        var recorder = new CapturingRecorder(tracked);
        var service = new SteamFriendsService(
            feed, new StubAppNameResolver(), RichPresenceFakes.NullCollector(feed, time), recorder, new NullEventBus(), store, time,
            NullLogger<SteamFriendsService>.Instance);
        return (service, feed, store, recorder);
    }

    private sealed class ScriptedPresenceFeed : ISteamPresenceFeed
    {
        private int _nicknameCalls;

        public SteamPresenceFeedHandlers? Handlers { get; private set; }
        public IReadOnlyList<SteamPersonaSnapshot> Friends { get; set; } = [];
        public TaskCompletionSource<IReadOnlyDictionary<ulong, string>?>? PendingNicknames { get; set; }
        public int NicknameCalls => Volatile.Read(ref _nicknameCalls);

        public IReadOnlyList<string> GetLoggedInAccounts() => [Account];

        public bool TryGetSnapshot(
            string accountName,
            out SteamPersonaSnapshot currentUser,
            out IReadOnlyList<SteamPersonaSnapshot> friends)
        {
            currentUser = new SteamPersonaSnapshot(Me, "Alice", 1, 0, 0, string.Empty);
            friends = Friends;
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
        {
            Interlocked.Increment(ref _nicknameCalls);
            return PendingNicknames?.Task
                ?? Task.FromResult<IReadOnlyDictionary<ulong, string>?>(new Dictionary<ulong, string>());
        }
    }

    private sealed class CapturingRecorder(bool tracked) : IFriendStatusRecorder
    {
        public List<(string ChangeType, FriendStatusValue? Previous, FriendStatusValue? Current)> Calls { get; } = [];

        public bool IsTracked(string accountName, string friendSteamId) => tracked;

        public Task RecordChangeAsync(
            string accountName,
            string friendSteamId,
            string friendPersonaName,
            string changeType,
            FriendStatusValue? previousValue,
            FriendStatusValue? currentValue,
            CancellationToken cancellationToken = default)
        {
            lock (Calls) Calls.Add((changeType, previousValue, currentValue));
            return Task.CompletedTask;
        }
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
