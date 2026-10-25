using ElectronNet.Models;

namespace ElectronNet.Features.History;

/// <summary>本地会话的最小视图（用于对账）</summary>
public readonly record struct LocalSessionSpan(int StartTime, int EndTime, int Duration);

/// <summary>单个 (SteamId, AppId) 的对账结果</summary>
public sealed record PlaytimeReconciliation(
    string SteamId,
    int AppId,
    int OfficialMinutes,
    int BaselineSeconds,
    int BaselineAt,
    int LastObservedAt,
    int? LastPlayedAt,
    int OfficialDeltaSeconds,
    int LocalTrackedSeconds,
    int LocalTotalSeconds,
    int UntrackedSeconds,
    string Status,
    IReadOnlyList<PlaytimeDiscrepancy> Discrepancies);

/// <summary>两次官方值变化之间，Steam 记录的时长明显多于本地会话的区间（疑似其他设备 / 未检测到的游玩）</summary>
public sealed record PlaytimeDiscrepancy(int From, int To, int OfficialSeconds, int LocalSeconds);

public static class PlaytimeReconciler
{
    public const string Matched = "matched";
    public const string Untracked = "untracked";
    public const string Overcounted = "overcounted";

    private const int MinimumToleranceSeconds = 15 * 60;

    /// <summary>
    /// 对账：首条快照为基线（安装前历史 = 官方值 − 基线时刻之前结束的本地会话），
    /// 之后官方增量与「在该区间内结束」的本地会话比较（Steam 在会话结束时累计时长）。
    /// </summary>
    public static PlaytimeReconciliation Reconcile(
        IReadOnlyList<PlaytimeSnapshot> snapshots,
        IReadOnlyList<LocalSessionSpan> sessions)
    {
        if (snapshots.Count == 0) throw new ArgumentException("At least one snapshot is required.", nameof(snapshots));
        var ordered = snapshots.OrderBy(s => s.FirstObservedAt).ThenBy(s => s.PlaytimeMinutes).ToList();
        var first = ordered[0];
        var last = ordered[^1];

        var localBefore = SumEndedIn(sessions, int.MinValue, first.FirstObservedAt);
        var baseline = Math.Max(0, first.PlaytimeMinutes * 60 - localBefore);
        var officialDelta = (last.PlaytimeMinutes - first.PlaytimeMinutes) * 60;
        var lastObserved = Math.Max(last.LastObservedAt, last.FirstObservedAt);
        var localTracked = SumEndedIn(sessions, first.FirstObservedAt, lastObserved);
        var untracked = officialDelta - localTracked;

        var discrepancies = new List<PlaytimeDiscrepancy>();
        for (var i = 1; i < ordered.Count; i++)
        {
            var from = ordered[i - 1].FirstObservedAt;
            var to = ordered[i].FirstObservedAt;
            var official = (ordered[i].PlaytimeMinutes - ordered[i - 1].PlaytimeMinutes) * 60;
            var local = SumEndedIn(sessions, from, to);
            if (official - local > Tolerance(official, local))
                discrepancies.Add(new PlaytimeDiscrepancy(from, to, official, local));
        }

        var tolerance = Tolerance(officialDelta, localTracked);
        var status = untracked > tolerance ? Untracked : untracked < -tolerance ? Overcounted : Matched;

        return new PlaytimeReconciliation(
            first.SteamId,
            first.AppId,
            last.PlaytimeMinutes,
            baseline,
            first.FirstObservedAt,
            lastObserved,
            ordered.Select(s => s.LastPlayedAt).Max(),
            officialDelta,
            localTracked,
            sessions.Sum(s => s.Duration),
            untracked,
            status,
            discrepancies);
    }

    private static int Tolerance(int official, int local)
        => Math.Max(MinimumToleranceSeconds, (int)(Math.Max(official, local) * 0.1));

    /// <summary>结束时间落在 (from, to] 的会话时长之和</summary>
    private static int SumEndedIn(IReadOnlyList<LocalSessionSpan> sessions, int from, int to)
        => sessions.Where(s => s.EndTime > from && s.EndTime <= to).Sum(s => s.Duration);
}

public static class PlaytimeSnapshotMerger
{
    /// <summary>
    /// 将观测合并进按 (SteamId, AppId) 索引的最新快照：值不变则延长观测区间，值增大则新开一段，值变小（陈旧来源）忽略。
    /// 返回需要插入的新行；被延长的行在原对象上修改。
    /// </summary>
    public static List<PlaytimeSnapshot> Merge(
        IDictionary<(string SteamId, int AppId), PlaytimeSnapshot> latest,
        IEnumerable<PlaytimeObservation> observations)
    {
        var inserted = new List<PlaytimeSnapshot>();
        foreach (var observation in observations.OrderBy(o => o.ObservedAt))
        {
            var key = (observation.SteamId, observation.AppId);
            if (latest.TryGetValue(key, out var current))
            {
                if (observation.PlaytimeMinutes < current.PlaytimeMinutes) continue;
                if (observation.PlaytimeMinutes == current.PlaytimeMinutes)
                {
                    current.LastObservedAt = Math.Max(current.LastObservedAt, observation.ObservedAt);
                    current.LastPlayedAt = Max(current.LastPlayedAt, observation.LastPlayedAt);
                    continue;
                }
            }

            var observedAt = current == null ? observation.ObservedAt : Math.Max(observation.ObservedAt, current.LastObservedAt);
            var row = new PlaytimeSnapshot
            {
                SteamId = observation.SteamId,
                AppId = observation.AppId,
                PlaytimeMinutes = observation.PlaytimeMinutes,
                Source = observation.Source,
                FirstObservedAt = observedAt,
                LastObservedAt = observedAt,
                LastPlayedAt = Max(current?.LastPlayedAt, observation.LastPlayedAt)
            };
            inserted.Add(row);
            latest[key] = row;
        }
        return inserted;
    }

    private static int? Max(int? a, int? b) => a == null ? b : b == null ? a : Math.Max(a.Value, b.Value);
}
