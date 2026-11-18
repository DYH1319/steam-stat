using SteamStat.Core.Features.Friends;
using SteamStat.Core.Insights;

namespace SteamStat.Core.Circle;

/// <summary>某人在某个时刻进入（或离开）一个分组。Key 为空表示离开。</summary>
public sealed record GroupObservation(string SteamId, int AppId, string Kind, string Key, long Time);

/// <summary>某人在一个分组里的一段时间。</summary>
public sealed record GroupSpan(string SteamId, int AppId, string Kind, string Key, long Start, long End);

public enum PartyVerdict
{
    /// <summary>两人在同一个大厅、服务器或 Rich Presence 组。</summary>
    Same,
    /// <summary>两人同一时间都有分组信息，但不是同一组：同时在玩，但不在一起。</summary>
    Different
}

public sealed record PartyWindow(int AppId, long Start, long End, PartyVerdict Verdict);

/// <summary>由分组变化推出每个人在分组里的时间段。</summary>
public static class GroupTimeline
{
    /// <summary>一次观测最多管这么久：之后没有新推送多半是应用没在运行，不能一直延续下去。</summary>
    public const long MaxSpanSeconds = 12 * 3600;

    public static IReadOnlyList<GroupSpan> Build(IEnumerable<GroupObservation> observations)
    {
        var spans = new List<GroupSpan>();
        foreach (var group in observations.GroupBy(item => (item.SteamId, item.Kind)))
        {
            GroupObservation? open = null;
            foreach (var item in group.OrderBy(item => item.Time))
            {
                if (open != null)
                {
                    if (item.Key == open.Key && item.AppId == open.AppId) continue;
                    Close(spans, open, item.Time);
                }
                open = item.Key.Length == 0 ? null : item;
            }
            if (open != null) Close(spans, open, long.MaxValue);
        }
        return spans;
    }

    private static void Close(List<GroupSpan> spans, GroupObservation open, long end)
    {
        var capped = Math.Min(end, open.Time + MaxSpanSeconds);
        if (capped > open.Time) spans.Add(new GroupSpan(open.SteamId, open.AppId, open.Kind, open.Key, open.Time, capped));
    }

    /// <summary>Rich Presence 的原始键值里取 steam_player_group。</summary>
    public static GroupObservation FromRichPresence(string steamId, int appId, IReadOnlyDictionary<string, string> values, long time)
    {
        var group = values.FirstOrDefault(pair => string.Equals(pair.Key, PresenceGroupKinds.RichPresenceKey, StringComparison.OrdinalIgnoreCase)).Value;
        return new GroupObservation(steamId, appId, PresenceGroupKinds.RichPresence, PresenceGroupKeys.RichPresence((uint)Math.Max(0, appId), group), time);
    }
}

public static class PartyEvidence
{
    /// <summary>
    /// 比较两个人的分组：同一种分组两边同时都有值时才有结论。相同优先：只要有一种分组相同就算在一起，
    /// 比如同一个大厅里的人进了不同服务器的情况不会被判成分开。
    /// </summary>
    public static IReadOnlyList<PartyWindow> Compare(IReadOnlyList<GroupSpan> mine, IReadOnlyList<GroupSpan> theirs)
    {
        var same = new Dictionary<int, List<Span>>();
        var different = new Dictionary<int, List<Span>>();
        foreach (var left in mine)
            foreach (var right in theirs)
            {
                if (left.Kind != right.Kind || left.AppId != right.AppId) continue;
                if (new Span(left.Start, left.End).Intersect(new Span(right.Start, right.End)) is not { } piece) continue;
                var target = left.Key == right.Key ? same : different;
                if (!target.TryGetValue(left.AppId, out var list)) target[left.AppId] = list = [];
                list.Add(piece);
            }
        var windows = new List<PartyWindow>();
        foreach (var (appId, spans) in same)
            windows.AddRange(Spans.Union(spans).Select(span => new PartyWindow(appId, span.Start, span.End, PartyVerdict.Same)));
        foreach (var (appId, spans) in different)
            windows.AddRange(Spans.Subtract(spans, same.GetValueOrDefault(appId) ?? []).Select(span => new PartyWindow(appId, span.Start, span.End, PartyVerdict.Different)));
        return windows.OrderBy(window => window.Start).ToList();
    }
}

/// <summary>我的一局游戏。</summary>
public sealed record TogetherSession(long Id, string SteamId, int AppId, long Start, long End);

/// <summary>
/// 一局游戏里和一位好友在一起的时间。<see cref="Seconds"/> 包含确认的部分：
/// 确认 = 同一组；其余是同时在玩同一款联机游戏、但没有分组信息可以判断（旧记录都是这种）。
/// </summary>
public sealed record TogetherSegment(long SessionId, string SteamId, string FriendSteamId, int AppId, long Start, long End, long Seconds, long ConfirmedSeconds)
{
    public bool Confirmed => ConfirmedSeconds > 0;
}

public sealed record TogetherResult(
    IReadOnlyList<TogetherSegment> Segments,
    /// <summary>每局和任一好友在一起的秒数，多位好友重叠只算一次。</summary>
    IReadOnlyDictionary<long, long> CoopSeconds,
    IReadOnlyDictionary<long, long> ConfirmedSeconds);

/// <summary>
/// 「一起玩」的判定。以前只看同一款联机游戏同时在玩；现在有分组信息时以分组为准：
/// 同一组算确认，两人都有分组但不同组的那段不算，其余仍按以前的口径。
/// </summary>
public static class TogetherDetector
{
    /// <summary>短于这个的重叠不记成一段（刚好擦肩的两局）。</summary>
    public const long MinSegmentSeconds = 5 * 60;

    public static TogetherResult Detect(
        IReadOnlyList<TogetherSession> sessions,
        InsightFriendData friends,
        IReadOnlyList<GroupSpan> groups,
        Func<int, bool> canPlayTogether)
    {
        var groupsBySteamId = groups.GroupBy(span => span.SteamId).ToDictionary(group => group.Key, group => (IReadOnlyList<GroupSpan>)group.ToList());
        var windowCache = new Dictionary<(string, string), IReadOnlyList<PartyWindow>>();
        IReadOnlyList<PartyWindow> WindowsOf(string me, string friend)
        {
            if (windowCache.TryGetValue((me, friend), out var cached)) return cached;
            var value = groupsBySteamId.TryGetValue(me, out var mine) && groupsBySteamId.TryGetValue(friend, out var theirs)
                ? PartyEvidence.Compare(mine, theirs)
                : [];
            windowCache[(me, friend)] = value;
            return value;
        }

        var segments = new List<TogetherSegment>();
        var coop = new Dictionary<long, long>();
        var confirmed = new Dictionary<long, long>();
        foreach (var session in sessions)
        {
            if (session.End <= session.Start) continue;
            var open = canPlayTogether(session.AppId);
            var sessionSpan = new Span(session.Start, session.End);
            var counted = new Dictionary<string, List<Span>>();
            var sure = new Dictionary<string, List<Span>>();
            foreach (var play in friends.PlaysOf(session.AppId))
            {
                if (play.SteamId == session.SteamId) continue;
                if (sessionSpan.Intersect(new Span(play.Start, play.End)) is not { } overlap) continue;
                var windows = WindowsOf(session.SteamId, play.SteamId).Where(window => window.AppId == session.AppId).ToList();
                var same = Spans.Intersect([overlap], windows.Where(window => window.Verdict == PartyVerdict.Same).Select(window => new Span(window.Start, window.End)));
                var apart = windows.Where(window => window.Verdict == PartyVerdict.Different).Select(window => new Span(window.Start, window.End));
                // 单机游戏只认确认的部分：没有分组信息时同时在玩不说明什么
                var count = open ? Spans.Subtract([overlap], apart) : same;
                Add(counted, play.SteamId, count);
                Add(sure, play.SteamId, same);
            }
            foreach (var (friend, spans) in counted)
            {
                var union = Spans.Union(spans);
                var seconds = union.Sum(span => span.Seconds);
                if (seconds < MinSegmentSeconds) continue;
                var confirmedSeconds = Spans.Total(sure.GetValueOrDefault(friend) ?? []);
                segments.Add(new TogetherSegment(session.Id, session.SteamId, friend, session.AppId, union[0].Start, union[^1].End, seconds, confirmedSeconds));
            }
            var all = Spans.Total(counted.Values.SelectMany(spans => spans));
            if (all > 0) coop[session.Id] = all;
            var allSure = Spans.Total(sure.Values.SelectMany(spans => spans));
            if (allSure > 0) confirmed[session.Id] = allSure;
        }
        return new TogetherResult(segments.OrderBy(segment => segment.Start).ToList(), coop, confirmed);
    }

    private static void Add(Dictionary<string, List<Span>> map, string key, IEnumerable<Span> spans)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        list.AddRange(spans);
    }
}
