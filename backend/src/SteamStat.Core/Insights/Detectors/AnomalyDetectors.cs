namespace SteamStat.Core.Insights;

/// <summary>时长异常：这段时间比之前的同长度时段高或低得不正常。</summary>
public sealed class PlaytimeSpikeDetector : IInsightDetector
{
    private const int ComparisonPeriods = 12;
    private const double NotableScore = 2;
    private const double MinimumHours = 5;

    public IEnumerable<Insight> Detect(InsightArchive archive, InsightPeriod period)
    {
        var length = InsightDays.Between(period.FromDay, period.ToDay) + 1;
        var previous = new List<double>();
        var sessions = 0;
        for (var index = 1; index <= ComparisonPeriods; index++)
        {
            var from = InsightDays.Shift(period.FromDay, -length * index);
            var list = archive.SessionsIn(from, InsightDays.Shift(from, length - 1)).ToList();
            previous.Add(InsightMath.Hours(list.Sum(session => session.Seconds)));
            sessions += list.Count;
        }
        // 档案还很新时前面全是零，任何一周都算异常
        if (previous.Count(hours => hours > 0) < ComparisonPeriods / 2) yield break;

        var hours = InsightMath.Hours(archive.SessionsIn(period).Sum(session => session.Seconds));
        var (mean, deviation) = InsightMath.MeanDeviation(previous);
        var score = (hours - mean) / Math.Max(deviation, 1);
        if (Math.Abs(score) < NotableScore || Math.Max(hours, mean) < MinimumHours) yield break;

        yield return new Insight(
            score > 0 ? "anomaly.playtimeSpike" : "anomaly.playtimeDrop",
            InsightKind.Anomaly,
            period.FromDay,
            period.ToDay,
            new Dictionary<string, double> { ["hours"] = hours, ["mean"] = mean, ["score"] = score },
            sessions,
            InsightMath.Clamp(0.5 + Math.Abs(score) * 0.1, 0, 0.95),
            [new InsightEvidence(InsightEvidenceLevel.Recorded, "v_session_day")]);
    }
}

/// <summary>官方差异：本地记录和 Steam 官方时长对不上的游戏占比偏高。对账本身在 PlaytimeHistoryService。</summary>
public sealed class OfficialDiffDetector : IInsightDetector
{
    private const double NotableShare = 0.1;

    public IEnumerable<Insight> Detect(InsightArchive archive, InsightPeriod period)
    {
        var reconciliation = archive.Reconciliation;
        if (reconciliation is not { Games: > 0 }) yield break;
        var share = (double)(reconciliation.Untracked + reconciliation.Overcounted) / reconciliation.Games;
        if (share < NotableShare) yield break;
        yield return new Insight(
            "anomaly.officialDiff",
            InsightKind.Anomaly,
            period.FromDay,
            period.ToDay,
            new Dictionary<string, double>
            {
                ["games"] = reconciliation.Games,
                ["untracked"] = reconciliation.Untracked,
                ["overcounted"] = reconciliation.Overcounted,
                ["share"] = share
            },
            reconciliation.Games,
            InsightMath.Clamp(0.6 + share * 0.5, 0, 0.9),
            [new InsightEvidence(InsightEvidenceLevel.Official, "v_official_playtime")]);
    }
}

/// <summary>记录断档：这段时间官方时长涨了，本地却没有会话。</summary>
public sealed class RecordGapDetector : IInsightDetector
{
    public IEnumerable<Insight> Detect(InsightArchive archive, InsightPeriod period)
    {
        var gaps = (archive.Reconciliation?.Gaps ?? [])
            .Where(gap => string.CompareOrdinal(gap.ToDay, period.FromDay) >= 0 && string.CompareOrdinal(gap.FromDay, period.ToDay) <= 0)
            .ToList();
        if (gaps.Count == 0) yield break;
        var days = gaps.Sum(gap => InsightDays.Between(gap.FromDay, gap.ToDay) + 1);
        yield return new Insight(
            "anomaly.recordGap",
            InsightKind.Anomaly,
            gaps.Min(gap => gap.FromDay)!,
            gaps.Max(gap => gap.ToDay)!,
            new Dictionary<string, double> { ["days"] = days, ["seconds"] = gaps.Sum(gap => gap.UntrackedSeconds) },
            days,
            0.75,
            [new InsightEvidence(InsightEvidenceLevel.Official, "v_official_playtime")])
        {
            IsEvent = true
        };
    }
}
