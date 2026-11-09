using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Features.Friends.Contracts;

namespace SteamStat.Core.Features.Friends;

public sealed record RichPresenceRecordEntry(
    string AccountName,
    string SteamId,
    bool IsSelf,
    uint AppId,
    string RawValues,
    string? DisplayText,
    string Source,
    long ObservedAt);

/// <summary>rich_presence_record 的读写。</summary>
public interface IRichPresenceRecorder
{
    /// <summary>每个玩家最后一条记录。</summary>
    Task<IReadOnlyList<RichPresenceBaseline>> GetLatestAsync(string accountName, CancellationToken cancellationToken = default);

    /// <summary>写入并返回记录 ID，失败时返回 null。</summary>
    Task<long?> RecordAsync(RichPresenceRecordEntry entry, CancellationToken cancellationToken = default);

    /// <summary>文案当时解析不出来，后来解析出来了。</summary>
    Task SetDisplayTextAsync(long recordId, string text, CancellationToken cancellationToken = default);
}

/// <summary>账号本人正在玩什么。CM 不一定把自己的状态发给这条会话，本机的会话记录更可靠。</summary>
public interface IRunningGameProbe
{
    uint? GetRunningAppId(string steamId);
}

public sealed record RichPresenceCoverage(
    long PushUpdates,
    long PollResponses,
    long RecordedChanges,
    long ChangesCaughtByPoll,
    int TrackedPresences,
    long Since,
    long? LastPushAt,
    long? LastPollAt);

/// <summary>采集管线需要 SteamFriendsService 提供的东西。</summary>
public sealed record RichPresenceHost(
    ulong SelfSteamId,
    /// <summary>当前在玩游戏的人（含账号本人），用于定时对账。</summary>
    Func<IReadOnlyList<(uint AppId, ulong SteamId)>> GetPlayers,
    /// <summary>把解析好的文案写回好友快照。null 表示解析不出来，保留现有文案。</summary>
    Action<ulong, uint, string?> ApplyText,
    Func<ulong, string?> GetPersonaName);

/// <summary>
/// 游戏内状态的采集管线，每个账号一条：
/// <code>
/// 推送 ┐
/// 轮询 ┼─▶ 有序队列 ─▶ 状态机 ─▶ 原始键值落库 ─▶ 解析文案 ─▶ 更新显示 / 好友时间线
/// 换游戏┘
/// </code>
/// <para>
/// 之前的问题和这里的对应：解析是并发的所以结果可能乱序（现在串行）；只有主动请求的回复才记录，
/// 推送来的变化只更新显示（现在都记录）；比较解析后的文案，拿不到本地化时变化就丢了（现在比较原始键值，
/// 文案解析不出来也先落库，之后再补）；漏掉推送没有任何补救（现在定时对账）。
/// </para>
/// </summary>
public sealed class RichPresenceCollector(
    ISteamPresenceFeed presenceFeed,
    IRichPresenceResolver resolver,
    IRichPresenceRecorder recorder,
    IFriendStatusRecorder friendStatusRecorder,
    TimeProvider timeProvider,
    ILogger<RichPresenceCollector> logger) : IAsyncDisposable
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan FirstPollDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(10);
    private const int MaxIdsPerRequest = 50;

    private readonly ConcurrentDictionary<string, Pipeline> _pipelines = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    private abstract record WorkItem;
    private sealed record ObservationItem(RichPresenceObservation Observation) : WorkItem;
    private sealed record GameChangeItem(ulong SteamId, uint AppId) : WorkItem;

    private sealed class Pipeline(string accountName, RichPresenceHost host, long startedAt) : IAsyncDisposable
    {
        public string AccountName { get; } = accountName;
        public RichPresenceHost Host { get; } = host;
        public long StartedAt { get; } = startedAt;
        public RichPresenceTracker Tracker { get; } = new();
        public Channel<WorkItem> Queue { get; } = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions { SingleReader = true });
        public CancellationTokenSource Stopping { get; } = new();
        public Task? Worker { get; set; }
        public Task? Poller { get; set; }
        public long PushUpdates;
        public long PollResponses;
        public long RecordedChanges;
        public long ChangesCaughtByPoll;
        public long LastPushAt;
        public long LastPollAt;
        public int TrackedPresences;

        public async ValueTask DisposeAsync()
        {
            Queue.Writer.TryComplete();
            await Stopping.CancelAsync().ConfigureAwait(false);
            foreach (var task in new[] { Worker, Poller })
            {
                if (task == null) continue;
                try
                {
                    await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
                {
                }
            }
            Stopping.Dispose();
        }
    }

    public void Start(string accountName, RichPresenceHost host)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var pipeline = new Pipeline(accountName, host, timeProvider.GetUtcNow().ToUnixTimeSeconds());
        if (_pipelines.TryRemove(accountName, out var previous)) _ = previous.DisposeAsync().AsTask();
        _pipelines[accountName] = pipeline;
        pipeline.Worker = Task.Run(() => RunWorkerAsync(pipeline));
        pipeline.Poller = Task.Run(() => RunPollerAsync(pipeline));
    }

    public async Task StopAsync(string accountName)
    {
        if (_pipelines.TryRemove(accountName, out var pipeline)) await pipeline.DisposeAsync().ConfigureAwait(false);
    }

    public void Observe(string accountName, SteamRichPresenceUpdate update)
    {
        if (!_pipelines.TryGetValue(accountName, out var pipeline)) return;
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        if (update.Source == RichPresenceSource.Push)
        {
            Interlocked.Increment(ref pipeline.PushUpdates);
            Interlocked.Exchange(ref pipeline.LastPushAt, now);
        }
        else
        {
            Interlocked.Increment(ref pipeline.PollResponses);
            Interlocked.Exchange(ref pipeline.LastPollAt, now);
        }
        pipeline.Queue.Writer.TryWrite(new ObservationItem(
            new RichPresenceObservation(update.SteamId, update.AppId, update.Values, update.Source, now)));
    }

    public void GameChanged(string accountName, ulong steamId, uint appId)
    {
        if (_pipelines.TryGetValue(accountName, out var pipeline))
            pipeline.Queue.Writer.TryWrite(new GameChangeItem(steamId, appId));
    }

    public RichPresenceCoverage? GetCoverage(string accountName)
    {
        if (!_pipelines.TryGetValue(accountName, out var pipeline)) return null;
        var lastPush = Interlocked.Read(ref pipeline.LastPushAt);
        var lastPoll = Interlocked.Read(ref pipeline.LastPollAt);
        return new RichPresenceCoverage(
            Interlocked.Read(ref pipeline.PushUpdates),
            Interlocked.Read(ref pipeline.PollResponses),
            Interlocked.Read(ref pipeline.RecordedChanges),
            Interlocked.Read(ref pipeline.ChangesCaughtByPoll),
            Volatile.Read(ref pipeline.TrackedPresences),
            pipeline.StartedAt,
            lastPush == 0 ? null : lastPush,
            lastPoll == 0 ? null : lastPoll);
    }

    public IReadOnlyList<string> GetAccounts() => _pipelines.Keys.ToArray();

    private async Task RunWorkerAsync(Pipeline pipeline)
    {
        var token = pipeline.Stopping.Token;
        try
        {
            try
            {
                pipeline.Tracker.Seed(await recorder.GetLatestAsync(pipeline.AccountName, token).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Failed to load the last rich presence records");
            }

            await foreach (var item in pipeline.Queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                try
                {
                    var decision = item switch
                    {
                        ObservationItem value => pipeline.Tracker.Apply(value.Observation),
                        GameChangeItem value => pipeline.Tracker.GameChanged(value.SteamId, value.AppId),
                        _ => null
                    };
                    if (decision != null) await ProcessAsync(pipeline, decision, token).ConfigureAwait(false);
                    Volatile.Write(ref pipeline.TrackedPresences, pipeline.Tracker.Count);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // 一条出错不能让整条管线停下来
                    logger.LogWarning(exception, "Failed to process a rich presence update");
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task ProcessAsync(Pipeline pipeline, RichPresenceDecision decision, CancellationToken cancellationToken)
    {
        var observation = decision.Observation;
        var current = decision.Current;
        var steamId = observation.SteamId;
        var isSelf = steamId == pipeline.Host.SelfSteamId;
        var steamIdText = steamId.ToString();
        var recordable = isSelf || friendStatusRecorder.IsTracked(pipeline.AccountName, steamIdText);
        var hadText = !string.IsNullOrEmpty(current.Text);

        if (decision.Changed && recordable)
        {
            // 先落库：原始键值不依赖本地化，解析失败或超时都不能丢
            current.RecordId = await recorder.RecordAsync(new RichPresenceRecordEntry(
                pipeline.AccountName, steamIdText, isSelf, current.AppId, current.RawValues, null,
                observation.Source == RichPresenceSource.Push ? "push" : "poll",
                observation.ObservedAt), cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref pipeline.RecordedChanges);
            if (observation.Source == RichPresenceSource.Poll && decision.Previous is { FromBaseline: false })
                Interlocked.Increment(ref pipeline.ChangesCaughtByPoll);
        }

        if (current.IsEmpty)
        {
            pipeline.Host.ApplyText(steamId, current.AppId, string.Empty);
            return;
        }
        if (!hadText)
        {
            var text = await ResolveTextAsync(pipeline.AccountName, current, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(text)) current.Text = text;
        }
        if (string.IsNullOrEmpty(current.Text))
        {
            // 本地化暂时拿不到：下一次轮询带着同样的状态回来时再试
            if (decision.Changed) pipeline.Host.ApplyText(steamId, current.AppId, null);
            return;
        }

        pipeline.Host.ApplyText(steamId, current.AppId, current.Text);
        if (hadText || !recordable) return;

        if (current.RecordId is { } recordId)
            await recorder.SetDisplayTextAsync(recordId, current.Text, cancellationToken).ConfigureAwait(false);
        if (isSelf) return;
        await friendStatusRecorder.RecordChangeAsync(
            pipeline.AccountName,
            steamIdText,
            pipeline.Host.GetPersonaName(steamId) ?? string.Empty,
            "richPresence",
            new FriendStatusValue(RichPresence: decision.Changed ? decision.Previous?.Text ?? string.Empty : string.Empty),
            new FriendStatusValue(RichPresence: current.Text),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ResolveTextAsync(string accountName, RichPresenceEntryState entry, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ResolveTimeout);
        try
        {
            return await resolver.ResolveAsync(accountName, entry.AppId, entry.Values, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("Rich presence localization timed out for app {AppId}", entry.AppId);
            return null;
        }
    }

    private async Task RunPollerAsync(Pipeline pipeline)
    {
        var token = pipeline.Stopping.Token;
        try
        {
            await Task.Delay(FirstPollDelay, timeProvider, token).ConfigureAwait(false);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    Poll(pipeline);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception, "Rich presence reconciliation failed");
                }
                await Task.Delay(PollInterval, timeProvider, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    internal void PollNow(string accountName)
    {
        if (_pipelines.TryGetValue(accountName, out var pipeline)) Poll(pipeline);
    }

    private void Poll(Pipeline pipeline)
    {
        foreach (var group in pipeline.Host.GetPlayers().GroupBy(player => player.AppId))
            foreach (var batch in group.Select(player => player.SteamId).Chunk(MaxIdsPerRequest))
                presenceFeed.RequestRichPresence(pipeline.AccountName, group.Key, batch);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var pipeline in _pipelines.Values) await pipeline.DisposeAsync().ConfigureAwait(false);
        _pipelines.Clear();
    }
}
