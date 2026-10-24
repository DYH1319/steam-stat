namespace ElectronNet.Features.UsageTracking;

/// <summary>
/// 跟踪单条进行中使用记录的空闲（AFK）与前台时长。
/// </summary>
public sealed class SessionActivityTracker(int recordId, string steamId, int appId, DateTimeOffset startedAt)
{
    private long _closedIdleSeconds;
    private DateTimeOffset? _openSpanStart;
    private long _foregroundSeconds;
    private bool _foregroundKnown;

    public int RecordId { get; } = recordId;
    public string SteamId { get; } = steamId;
    public int AppId { get; } = appId;
    public DateTimeOffset StartedAt { get; } = startedAt;
    public DateTimeOffset LastObservedAt { get; private set; } = startedAt;
    public DateTimeOffset LastFlushedAt { get; set; } = startedAt;

    public void Observe(DateTimeOffset now, TimeSpan? idle, bool? isForeground, bool afkEnabled, TimeSpan afkThreshold)
    {
        var delta = Math.Max(0, (now - LastObservedAt).TotalSeconds);

        if (isForeground == true) _foregroundSeconds += (long)delta;
        if (isForeground != null) _foregroundKnown = true;

        if (afkEnabled && idle.HasValue && idle.Value >= afkThreshold)
        {
            // 回溯：阈值内的那段时间也算 AFK
            var spanStart = now - idle.Value;
            if (spanStart < StartedAt) spanStart = StartedAt;
            _openSpanStart ??= spanStart;
        }
        else if (_openSpanStart.HasValue)
        {
            var spanEnd = afkEnabled && idle.HasValue ? now - idle.Value : now;
            if (spanEnd < _openSpanStart.Value) spanEnd = _openSpanStart.Value;
            _closedIdleSeconds += (long)Math.Max(0, (spanEnd - _openSpanStart.Value).TotalSeconds);
            _openSpanStart = null;
        }

        LastObservedAt = now;
    }

    public long IdleSecondsAt(DateTimeOffset at)
    {
        var idle = _closedIdleSeconds;
        if (_openSpanStart.HasValue)
        {
            idle += (long)Math.Max(0, (at - _openSpanStart.Value).TotalSeconds);
        }
        var elapsed = (long)Math.Max(0, (at - StartedAt).TotalSeconds);
        return Math.Clamp(idle, 0, elapsed);
    }

    public long? ForegroundSeconds
    {
        get
        {
            if (!_foregroundKnown) return null;
            var elapsed = (long)Math.Max(0, (LastObservedAt - StartedAt).TotalSeconds);
            return Math.Clamp(_foregroundSeconds, 0, elapsed);
        }
    }
}
