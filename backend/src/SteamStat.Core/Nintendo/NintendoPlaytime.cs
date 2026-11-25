namespace SteamStat.Core.Nintendo;

/// <summary>
/// 累计时长的一段「平台期」：在 [FirstObservedAt, LastObservedAt] 内每次读到的累计分钟数都是 Minutes。
/// 和 Steam 的 playtime_snapshot 一样按值变化分段，第一段就是绑定之前的基线。
/// </summary>
public sealed record NintendoSegment(string TitleId, string Platform, int Minutes, long FirstObservedAt, long LastObservedAt, long? LastPlayedAt);

/// <summary>由两段之间的增量推算出来的一段游玩。Inferred：任天堂不公开起止时间，这只是「大约这个时候在玩」。</summary>
public sealed record NintendoInterval(string TitleId, string Platform, long Start, long End, int Minutes);

public sealed record NintendoInference(IReadOnlyList<NintendoInterval> Intervals, int PlacedMinutes, int UnplacedMinutes);

/// <summary>
/// 推算「大约几点在玩」。只用于时段分布，任何时长合计都以官方日级数据为准。
/// 两次读取之间累计值多了 Δ 分钟：窗口足够短（应用一直开着、按时在读）且装得下 Δ 时才把它放到具体时间上，
/// 否则（应用关着、主机很晚才上传）这段算作放不上的，页面用覆盖率说明。
/// </summary>
public static class NintendoPlaytimeInference
{
    /// <summary>窗口超过这么长就不放：中间可能是好几局，甚至跨了好几天。</summary>
    public static readonly long MaxWindowSeconds = (long)TimeSpan.FromHours(4).TotalSeconds;

    /// <summary>相邻两段间隔不超过这么久就连成一局。</summary>
    public static readonly long MergeGapSeconds = (long)TimeSpan.FromMinutes(20).TotalSeconds;

    public static NintendoInference Infer(IEnumerable<NintendoSegment> segments)
    {
        var intervals = new List<NintendoInterval>();
        var placed = 0;
        var unplaced = 0;
        foreach (var group in segments.GroupBy(segment => (segment.TitleId, segment.Platform)))
        {
            NintendoSegment? previous = null;
            foreach (var current in group.OrderBy(segment => segment.FirstObservedAt).ThenBy(segment => segment.Minutes))
            {
                if (previous != null && current.Minutes > previous.Minutes)
                {
                    var delta = current.Minutes - previous.Minutes;
                    if (Place(previous.LastObservedAt, current.FirstObservedAt, delta, current.LastPlayedAt) is { } span)
                    {
                        intervals.Add(new NintendoInterval(group.Key.TitleId, group.Key.Platform, span.Start, span.End, delta));
                        placed += delta;
                    }
                    else
                    {
                        unplaced += delta;
                    }
                }
                // 累计值变小（任天堂改正了数据）时从新值重新算起
                previous = current;
            }
        }
        return new NintendoInference(Merge(intervals), placed, unplaced);
    }

    /// <summary>
    /// 窗口是 (after, before]。最近游玩时间落在窗口里时以它为准：能往前放就当它是结束时间，
    /// 放不下就当它是开始时间；都不行或者没有就贴着这次读取往前放。
    /// </summary>
    internal static (long Start, long End)? Place(long after, long before, int minutes, long? lastPlayedAt)
    {
        var window = before - after;
        var length = minutes * 60L;
        if (window <= 0 || window > MaxWindowSeconds || length > window) return null;
        if (lastPlayedAt is { } last && last > after && last <= before)
        {
            if (last - length >= after) return (last - length, last);
            if (last + length <= before) return (last, last + length);
        }
        return (before - length, before);
    }

    internal static IReadOnlyList<NintendoInterval> Merge(IEnumerable<NintendoInterval> intervals)
    {
        var merged = new List<NintendoInterval>();
        foreach (var group in intervals.GroupBy(interval => (interval.TitleId, interval.Platform)))
        {
            NintendoInterval? open = null;
            foreach (var interval in group.OrderBy(interval => interval.Start))
            {
                if (open != null && interval.Start - open.End <= MergeGapSeconds)
                {
                    open = open with { End = Math.Max(open.End, interval.End), Minutes = open.Minutes + interval.Minutes };
                    continue;
                }
                if (open != null) merged.Add(open);
                open = interval;
            }
            if (open != null) merged.Add(open);
        }
        return merged.OrderBy(interval => interval.Start).ThenBy(interval => interval.TitleId, StringComparer.Ordinal).ToList();
    }
}
