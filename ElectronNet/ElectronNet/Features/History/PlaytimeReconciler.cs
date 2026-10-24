namespace ElectronNet.Features.History;

/// <summary>
/// 单一来源的一条官方累计时长快照
/// </summary>
public sealed record SnapshotPoint(int PlaytimeMinutes, int CapturedAt, int LastSeenAt, bool IsBaseline);

/// <summary>
/// 本地会话（原始行，不做清洗）
/// </summary>
public sealed record LocalSessionSpan(int Start, int End, int Duration);

/// <summary>
/// 相邻两次快照之间的差异明细
/// </summary>
public sealed record ReconcileInterval(
    int From,
    int To,
    long OfficialDeltaSeconds,
    long LocalSeconds,
    long UnaccountedSeconds);

/// <summary>
/// 单个（用户, 应用）在某来源下的对账结果
/// </summary>
public sealed record ReconcileResult(
    int BaselineMinutes,
    int BaselineCapturedAt,
    int OfficialMinutes,
    int OfficialSeenAt,
    int SnapshotCount,
    long PreTrackingSeconds,
    long OfficialDeltaSeconds,
    long LocalSinceBaselineSeconds,
    long UnaccountedSeconds,
    string Status,
    IReadOnlyList<ReconcileInterval> Intervals);

/// <summary>
/// 对比官方累计时长快照与本地会话，识别在本机之外的游玩
/// </summary>
public static class PlaytimeReconciler
{
    /// <summary>
    /// 对单个（steamId, appId）的某一来源执行对账
    /// </summary>
    /// <param name="snapshots">同一来源的快照，按 CapturedAt 升序</param>
    /// <param name="sessions">本地会话（已过滤为 EndTime != null 且 Duration > 0 的原始行）</param>
    public static ReconcileResult Reconcile(
        IReadOnlyList<SnapshotPoint> snapshots,
        IReadOnlyList<LocalSessionSpan> sessions)
    {
        if (snapshots.Count == 0)
            throw new ArgumentException("At least one snapshot is required.", nameof(snapshots));

        var baseline = snapshots.FirstOrDefault(s => s.IsBaseline) ?? snapshots[0];
        var latest = snapshots[^1];

        var preTrackingSeconds = Math.Max(
            0,
            baseline.PlaytimeMinutes * 60L - LocalSeconds(sessions, long.MinValue, baseline.CapturedAt));
        var officialDeltaSeconds = (latest.PlaytimeMinutes - baseline.PlaytimeMinutes) * 60L;
        var localSinceBaselineSeconds = LocalSeconds(sessions, baseline.CapturedAt, latest.LastSeenAt);
        var unaccountedSeconds = officialDeltaSeconds - localSinceBaselineSeconds;
        var tolerance = Tolerance(officialDeltaSeconds);

        var status = ReferenceEquals(latest, baseline) && localSinceBaselineSeconds == 0
            ? "baselineOnly"
            : unaccountedSeconds > tolerance
                ? "elsewhere"
                : unaccountedSeconds < -tolerance
                    ? "localExceeds"
                    : "matched";

        var intervals = new List<ReconcileInterval>();
        var baselineIndex = 0;
        for (var i = 0; i < snapshots.Count; i++)
        {
            if (ReferenceEquals(snapshots[i], baseline))
            {
                baselineIndex = i;
                break;
            }
        }
        for (var i = baselineIndex + 1; i < snapshots.Count; i++)
        {
            var prev = snapshots[i - 1];
            var cur = snapshots[i];
            var delta = (cur.PlaytimeMinutes - prev.PlaytimeMinutes) * 60L;
            var local = LocalSeconds(sessions, prev.LastSeenAt, cur.CapturedAt);
            var unaccounted = delta - local;
            if (Math.Abs(unaccounted) <= Tolerance(delta)) continue;
            intervals.Add(new ReconcileInterval(
                prev.LastSeenAt,
                cur.CapturedAt,
                delta,
                local,
                unaccounted));
        }

        return new ReconcileResult(
            baseline.PlaytimeMinutes,
            baseline.CapturedAt,
            latest.PlaytimeMinutes,
            latest.LastSeenAt,
            snapshots.Count,
            preTrackingSeconds,
            officialDeltaSeconds,
            localSinceBaselineSeconds,
            unaccountedSeconds,
            status,
            intervals);
    }

    /// <summary>
    /// [from, to) 内按比例分摊的本地会话秒数
    /// </summary>
    internal static long LocalSeconds(IEnumerable<LocalSessionSpan> sessions, long from, long to)
    {
        var total = 0L;
        foreach (var session in sessions)
        {
            var span = session.End - (long)session.Start;
            if (span <= 0) continue;
            var clip = Math.Max(0, Math.Min(session.End, to) - Math.Max(session.Start, from));
            if (clip == 0) continue;
            total += (long)Math.Round(clip * (session.Duration / (double)span), MidpointRounding.AwayFromZero);
        }
        return total;
    }

    internal static long Tolerance(long delta)
        => Math.Max(900, (long)(0.05 * Math.Abs(delta)));
}
