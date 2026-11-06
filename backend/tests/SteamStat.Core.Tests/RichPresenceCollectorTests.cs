using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Core.Features;
using SteamStat.Core.Features.Friends;
using SteamStat.Core.Features.Friends.Contracts;

namespace SteamStat.Core.Tests;

internal static class RichPresenceFakes
{
    public static RichPresenceCollector NullCollector(ISteamPresenceFeed feed, TimeProvider time)
        => new(feed, new ScriptedResolver(), new MemoryRecorder(), new TrackingRecorder(), time, NullLogger<RichPresenceCollector>.Instance);

    internal sealed class ScriptedResolver : IRichPresenceResolver
    {
        /// <summary>本地化是否可用。不可用时和真实解析器一样返回空文案。</summary>
        public bool Available { get; set; } = true;

        /// <summary>按 display 值决定解析耗时，用来验证结果不会乱序。</summary>
        public Func<string, TimeSpan> Delay { get; set; } = _ => TimeSpan.Zero;

        public async Task<string> ResolveAsync(string accountName, uint appId, IReadOnlyDictionary<string, string> richPresence, CancellationToken cancellationToken = default)
        {
            var display = richPresence.GetValueOrDefault("steam_display", string.Empty);
            var delay = Delay(display);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            return Available ? $"text:{display}" : string.Empty;
        }
    }

    internal sealed class MemoryRecorder : IRichPresenceRecorder
    {
        private long _nextId;
        public ConcurrentQueue<(long Id, RichPresenceRecordEntry Entry)> Records { get; } = new();
        public ConcurrentDictionary<long, string> Texts { get; } = new();
        public List<RichPresenceBaseline> Baselines { get; } = [];

        public Task<IReadOnlyList<RichPresenceBaseline>> GetLatestAsync(string accountName, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RichPresenceBaseline>>(Baselines);

        public Task<long?> RecordAsync(RichPresenceRecordEntry entry, CancellationToken cancellationToken = default)
        {
            var id = Interlocked.Increment(ref _nextId);
            Records.Enqueue((id, entry));
            return Task.FromResult<long?>(id);
        }

        public Task SetDisplayTextAsync(long recordId, string text, CancellationToken cancellationToken = default)
        {
            Texts[recordId] = text;
            return Task.CompletedTask;
        }
    }

    internal sealed class TrackingRecorder : IFriendStatusRecorder
    {
        public HashSet<string> Tracked { get; } = [];
        public ConcurrentQueue<(string SteamId, string? Previous, string? Current)> Timeline { get; } = new();

        public bool IsTracked(string accountName, string friendSteamId) => Tracked.Contains(friendSteamId);

        public Task RecordChangeAsync(string accountName, string friendSteamId, string friendPersonaName, string changeType,
            FriendStatusValue? previousValue, FriendStatusValue? currentValue, CancellationToken cancellationToken = default)
        {
            if (changeType == "richPresence") Timeline.Enqueue((friendSteamId, previousValue?.RichPresence, currentValue?.RichPresence));
            return Task.CompletedTask;
        }
    }
}

/// <summary>状态机本身：只看原始键值，推送和轮询没有区别。</summary>
[TestFixture]
public sealed class RichPresenceTrackerTests
{
    private const ulong Bob = 76561198000000001;

    private static IReadOnlyDictionary<string, string> Values(params (string Key, string Value)[] items)
        => items.ToDictionary(item => item.Key, item => item.Value);

    private static RichPresenceObservation Seen(IReadOnlyDictionary<string, string> values, uint? appId = 730, RichPresenceSource source = RichPresenceSource.Push)
        => new(Bob, appId, values, source, 1000);

    [Test]
    public void SameValues_AreNotAChange_RegardlessOfChannelOrKeyOrder()
    {
        var tracker = new RichPresenceTracker();

        tracker.Apply(Seen(Values(("a", "1"), ("b", "2"))))!.Changed.Should().BeTrue();
        tracker.Apply(Seen(Values(("b", "2"), ("a", "1")), source: RichPresenceSource.Poll))!.Changed.Should().BeFalse();
    }

    [Test]
    public void AChangedJoinTokenAloneIsNotAChange()
    {
        var tracker = new RichPresenceTracker();
        tracker.Apply(Seen(Values(("status", "Lobby"), ("connect", "+token1"))));

        var decision = tracker.Apply(Seen(Values(("status", "Lobby"), ("connect", "+token2"))));

        decision!.Changed.Should().BeFalse();
    }

    [Test]
    public void ValuesThatArriveBeforeTheGame_WaitForIt()
    {
        var tracker = new RichPresenceTracker();

        tracker.Apply(Seen(Values(("status", "Ranked")), appId: null)).Should().BeNull();
        var decision = tracker.GameChanged(Bob, 730);

        decision!.Changed.Should().BeTrue();
        decision.Current.AppId.Should().Be(730);
    }

    [Test]
    public void ChangingGames_DropsTheOldStateWithoutRecordingAClear()
    {
        var tracker = new RichPresenceTracker();
        tracker.Apply(Seen(Values(("status", "Ranked"))));

        tracker.GameChanged(Bob, 570).Should().BeNull();
        tracker.Apply(Seen(Values(), appId: 570, source: RichPresenceSource.Poll)).Should().BeNull();
        tracker.Count.Should().Be(0);
    }

    [Test]
    public void OneEmptyPollIsNotEnoughToClear()
    {
        var tracker = new RichPresenceTracker();
        tracker.Apply(Seen(Values(("status", "Ranked"))));

        tracker.Apply(Seen(Values(), source: RichPresenceSource.Poll)).Should().BeNull();
        tracker.Apply(Seen(Values(("status", "Ranked"))))!.Changed.Should().BeFalse("一次空回复后状态又回来了，不算变化");

        tracker.Apply(Seen(Values(), source: RichPresenceSource.Poll)).Should().BeNull("计数已经被重置");
        var cleared = tracker.Apply(Seen(Values(), source: RichPresenceSource.Poll));
        cleared!.Changed.Should().BeTrue();
        cleared.Current.IsEmpty.Should().BeTrue();
    }

    [Test]
    public void TheLastRecordedStateIsNotRecordedAgainAfterARestart()
    {
        var tracker = new RichPresenceTracker();
        tracker.Seed([new RichPresenceBaseline(Bob, 730, RichPresenceTracker.Canonicalize(Values(("status", "Ranked"))), "Ranked", 7)]);

        var decision = tracker.Apply(Seen(Values(("status", "Ranked"))));

        decision!.Changed.Should().BeFalse();
        decision.Current.Text.Should().Be("Ranked");
    }

    [Test]
    public void AChangeSinceTheLastRunIsRecorded()
    {
        var tracker = new RichPresenceTracker();
        tracker.Seed([new RichPresenceBaseline(Bob, 730, RichPresenceTracker.Canonicalize(Values(("status", "Ranked"))), "Ranked", 7)]);

        tracker.Apply(Seen(Values(("status", "Casual"))))!.Changed.Should().BeTrue();
    }
}

/// <summary>管线：这些测试对应 Rich Presence 不稳定时的具体现象。</summary>
[TestFixture]
public sealed class RichPresenceCollectorTests
{
    private const string Account = "alice";
    private const ulong Me = 76561198000000000;
    private const ulong Bob = 76561198000000001;
    private const ulong Carol = 76561198000000002;

    private RichPresenceFakes.ScriptedResolver _resolver = null!;
    private RichPresenceFakes.MemoryRecorder _recorder = null!;
    private RichPresenceFakes.TrackingRecorder _tracking = null!;
    private FakeFeed _feed = null!;
    private RichPresenceCollector _collector = null!;
    private ConcurrentDictionary<ulong, string> _display = null!;
    private List<(uint, ulong)> _players = null!;

    [SetUp]
    public void SetUp()
    {
        _resolver = new RichPresenceFakes.ScriptedResolver();
        _recorder = new RichPresenceFakes.MemoryRecorder();
        _tracking = new RichPresenceFakes.TrackingRecorder();
        _tracking.Tracked.Add(Bob.ToString());
        _feed = new FakeFeed();
        _display = new ConcurrentDictionary<ulong, string>();
        _players = [];
        _collector = new RichPresenceCollector(_feed, _resolver, _recorder, _tracking, TimeProvider.System, NullLogger<RichPresenceCollector>.Instance);
    }

    [TearDown]
    public async Task TearDown() => await _collector.DisposeAsync();

    private void Start() => _collector.Start(Account, new RichPresenceHost(
        Me,
        () => _players,
        (steamId, _, text) => { if (text != null) _display[steamId] = text; },
        _ => "Bob"));

    private void Send(ulong steamId, string display, RichPresenceSource source = RichPresenceSource.Push, uint? appId = 730)
        => _collector.Observe(Account, new SteamRichPresenceUpdate(steamId, appId, new Dictionary<string, string> { ["steam_display"] = display }, source));

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 400 && !condition(); attempt++) await Task.Delay(5);
        condition().Should().BeTrue();
    }

    /// <summary>等队列处理完：发一条探针，它落库说明之前的都处理过了。</summary>
    private async Task SettleAsync()
    {
        var probe = 76561198000000099UL;
        _tracking.Tracked.Add(probe.ToString());
        var before = _recorder.Records.Count;
        _collector.Observe(Account, new SteamRichPresenceUpdate(probe, 1, new Dictionary<string, string> { ["p"] = Guid.NewGuid().ToString() }, RichPresenceSource.Push));
        await EventuallyAsync(() => _recorder.Records.Count > before);
        _tracking.Tracked.Remove(probe.ToString());
    }

    private List<(string SteamId, string? Previous, string? Current)> BobTimeline()
        => _tracking.Timeline.Where(item => item.SteamId == Bob.ToString()).ToList();

    private List<RichPresenceRecordEntry> Recorded(ulong steamId)
        => _recorder.Records.Select(item => item.Entry).Where(entry => entry.SteamId == steamId.ToString()).ToList();

    [Test]
    public async Task PushedChanges_AreRecorded()
    {
        Start();

        Send(Bob, "#Ranked");
        Send(Bob, "#Casual");
        await SettleAsync();

        Recorded(Bob).Select(entry => entry.Source).Should().Equal("push", "push");
        await EventuallyAsync(() => BobTimeline().Count == 2);
        BobTimeline()[^1].Should().Be((Bob.ToString(), "text:#Ranked", "text:#Casual"));
    }

    [Test]
    public async Task AChangeThePushMissed_IsCaughtByThePollAndCounted()
    {
        Start();
        Send(Bob, "#Ranked");
        await SettleAsync();

        Send(Bob, "#Casual", RichPresenceSource.Poll);
        await SettleAsync();

        Recorded(Bob).Should().HaveCount(2);
        Recorded(Bob)[1].Source.Should().Be("poll");
        _collector.GetCoverage(Account)!.ChangesCaughtByPoll.Should().Be(1);
    }

    [Test]
    public async Task ThePollConfirmingWhatThePushSaid_AddsNoRecord()
    {
        Start();
        Send(Bob, "#Ranked");
        Send(Bob, "#Ranked", RichPresenceSource.Poll);
        Send(Bob, "#Ranked", RichPresenceSource.Poll);
        await SettleAsync();

        Recorded(Bob).Should().ContainSingle();
    }

    [Test]
    public async Task WithoutLocalization_RawValuesAreStillStoredAndTextFollowsLater()
    {
        _resolver.Available = false;
        Start();

        Send(Bob, "#Ranked");
        await SettleAsync();

        var (id, entry) = _recorder.Records.Single(item => item.Entry.SteamId == Bob.ToString());
        entry.RawValues.Should().Contain("#Ranked");
        entry.DisplayText.Should().BeNull();
        BobTimeline().Should().BeEmpty();

        _resolver.Available = true;
        Send(Bob, "#Ranked", RichPresenceSource.Poll);
        await EventuallyAsync(() => _recorder.Texts.ContainsKey(id));

        _recorder.Texts[id].Should().Be("text:#Ranked");
        Recorded(Bob).Should().ContainSingle("文案补上不是新的状态变化");
        await EventuallyAsync(() => BobTimeline().Count == 1);
        _display[Bob].Should().Be("text:#Ranked");
    }

    [Test]
    public async Task SlowLocalization_CannotReorderUpdates()
    {
        _resolver.Delay = display => display == "#First" ? TimeSpan.FromMilliseconds(150) : TimeSpan.Zero;
        Start();

        Send(Bob, "#First");
        Send(Bob, "#Second");
        await EventuallyAsync(() => BobTimeline().Count == 2);

        _display[Bob].Should().Be("text:#Second");
        Recorded(Bob).Select(entry => entry.RawValues).Should().ContainInOrder(
            RichPresenceTracker.Canonicalize(new Dictionary<string, string> { ["steam_display"] = "#First" }),
            RichPresenceTracker.Canonicalize(new Dictionary<string, string> { ["steam_display"] = "#Second" }));
    }

    [Test]
    public async Task OwnStatus_IsRecordedWithoutBeingTracked()
    {
        Start();

        Send(Me, "#Solo");
        await SettleAsync();

        var entry = Recorded(Me).Single();
        entry.IsSelf.Should().BeTrue();
        _tracking.Timeline.Should().NotContain(item => item.SteamId == Me.ToString());
    }

    [Test]
    public async Task UntrackedFriends_AreShownButNotStored()
    {
        Start();

        Send(Carol, "#Ranked");
        await EventuallyAsync(() => _display.ContainsKey(Carol));
        await SettleAsync();

        Recorded(Carol).Should().BeEmpty();
    }

    [Test]
    public async Task Restarting_DoesNotRecordTheSameStateAgain()
    {
        _recorder.Baselines.Add(new RichPresenceBaseline(
            Bob, 730, RichPresenceTracker.Canonicalize(new Dictionary<string, string> { ["steam_display"] = "#Ranked" }), "Ranked", 42));
        Start();

        Send(Bob, "#Ranked", RichPresenceSource.Poll);
        await EventuallyAsync(() => _display.ContainsKey(Bob));

        _display[Bob].Should().Be("Ranked");
        await SettleAsync();
        Recorded(Bob).Should().BeEmpty();
    }

    [Test]
    public async Task APollAfterStartup_IsNotCountedAsAMissedPush()
    {
        _recorder.Baselines.Add(new RichPresenceBaseline(
            Bob, 730, RichPresenceTracker.Canonicalize(new Dictionary<string, string> { ["steam_display"] = "#Old" }), "Old", 42));
        Start();

        Send(Bob, "#New", RichPresenceSource.Poll);
        await SettleAsync();

        Recorded(Bob).Should().ContainSingle();
        _collector.GetCoverage(Account)!.ChangesCaughtByPoll.Should().Be(0);
    }

    [Test]
    public void Polling_BatchesPlayersByGame()
    {
        _players = [(730, Bob), (730, Carol), (570, Me)];
        Start();

        _collector.PollNow(Account);

        _feed.Requests.Should().BeEquivalentTo(new[]
        {
            (730u, new[] { Bob, Carol }),
            (570u, new[] { Me })
        });
    }

    private sealed class FakeFeed : ISteamPresenceFeed
    {
        public List<(uint AppId, ulong[] SteamIds)> Requests { get; } = [];
        public IReadOnlyList<string> GetLoggedInAccounts() => [Account];
        public bool TryGetSnapshot(string accountName, out SteamPersonaSnapshot currentUser, out IReadOnlyList<SteamPersonaSnapshot> friends)
        {
            currentUser = null!;
            friends = [];
            return false;
        }
        public IDisposable? Subscribe(string accountName, SteamPresenceFeedHandlers handlers) => null;
        public void RequestRichPresence(string accountName, uint appId, IReadOnlyList<ulong> steamIds) => Requests.Add((appId, steamIds.ToArray()));
        public void RequestLevels(string accountName, IReadOnlyList<ulong> steamIds) { }
        public void RequestFriendInfo(string accountName, ulong steamId) { }
        public Task<IReadOnlyDictionary<ulong, string>?> GetNicknamesAsync(string accountName, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<ulong, string>?>(null);
    }
}
