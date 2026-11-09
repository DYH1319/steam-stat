namespace SteamStat.Core.Insights;

/// <summary>积压库清算时间：按过去一年开始新游戏的速度，把积压库玩完要多久。</summary>
public sealed class BacklogClearanceDetector : IInsightDetector
{
    public const int BacklogMinutes = 60;
    private const double StartedHours = 2;
    private const int MinimumBacklog = 10;

    /// <summary>积压：玩了不到一小时，且没有标记通关或弃坑。和渲染层 isBacklog 一致。</summary>
    public static IEnumerable<InsightOwnedGame> Backlog(InsightArchive archive)
    {
        var closed = archive.Statuses.Where(item => item.Status is "completed" or "abandoned").Select(item => item.AppId).ToHashSet();
        return archive.Owned.Where(game => game.PlaytimeMinutes < BacklogMinutes && !closed.Contains(game.AppId));
    }

    public IEnumerable<Insight> Detect(InsightArchive archive, InsightPeriod period)
    {
        var backlog = Backlog(archive).Count();
        if (backlog < MinimumBacklog) yield break;
        var from = InsightDays.Shift(period.ToDay, -364);
        var started = archive.Sessions
            .GroupBy(session => session.AppId)
            .Count(group => string.CompareOrdinal(group.Min(item => item.Day)!, from) >= 0
                            && InsightMath.Hours(group.Sum(item => item.Seconds)) >= StartedHours);
        if (started == 0) yield break;

        yield return new Insight(
            "forecast.backlogClearance",
            InsightKind.Forecast,
            from,
            period.ToDay,
            new Dictionary<string, double> { ["backlog"] = backlog, ["perYear"] = started, ["years"] = (double)backlog / started },
            started,
            InsightMath.Clamp(0.55 + started / 100.0, 0, 0.9),
            [
                new InsightEvidence(InsightEvidenceLevel.Official, "library"),
                new InsightEvidence(InsightEvidenceLevel.Derived, "v_app_playtime")
            ]);
    }
}

/// <summary>全成就日期：最近三个月解锁得够多的游戏，按这个节奏什么时候打完。</summary>
public sealed class CompletionDateDetector : IInsightDetector
{
    private const int WindowDays = 90;
    private const double MinimumProgress = 0.3;
    private const int MaximumResults = 3;

    public IEnumerable<Insight> Detect(InsightArchive archive, InsightPeriod period)
    {
        var from = InsightDays.Shift(period.ToDay, -(WindowDays - 1));
        var recent = archive.Unlocks
            .Where(unlock => string.CompareOrdinal(unlock.Day, from) >= 0 && string.CompareOrdinal(unlock.Day, period.ToDay) <= 0)
            .GroupBy(unlock => unlock.AppId)
            .ToDictionary(group => group.Key, group => group.Count());

        var candidates = archive.Progress
            .GroupBy(progress => progress.AppId)
            .Select(group => group.MaxBy(item => item.Unlocked)!)
            .Where(progress => progress.Total > 0 && progress.Unlocked < progress.Total && (double)progress.Unlocked / progress.Total >= MinimumProgress)
            .Select(progress => (Progress: progress, Recent: recent.GetValueOrDefault(progress.AppId)))
            .Where(item => item.Recent > 0)
            .Select(item => (item.Progress, item.Recent, Days: (item.Progress.Total - item.Progress.Unlocked) / ((double)item.Recent / WindowDays)))
            .OrderBy(item => item.Days)
            .Take(MaximumResults);

        foreach (var (progress, count, days) in candidates)
        {
            yield return new Insight(
                "forecast.completionDate",
                InsightKind.Forecast,
                from,
                period.ToDay,
                new Dictionary<string, double>
                {
                    ["remaining"] = progress.Total - progress.Unlocked,
                    ["days"] = days,
                    ["recent"] = count,
                    ["total"] = progress.Total,
                    ["unlocked"] = progress.Unlocked
                },
                count,
                InsightMath.Clamp(0.6 + count / 100.0, 0, 0.9),
                [new InsightEvidence(InsightEvidenceLevel.Derived, "v_achievement_unlock", $"app_id = {progress.AppId}")])
            {
                AppId = progress.AppId,
                AppName = progress.Name
            };
        }
    }
}

/// <summary>
/// 弃坑概率：用自己的历史拟合。看过去所有「已经这么久没碰」的时刻，后来有多少再也没回去。
/// 还在等待中的间隔不知道结果，只有已经超过弃坑天数的才算数。
/// </summary>
public sealed class AbandonRiskDetector : IInsightDetector
{
    private const int MinimumIdleDays = 7;
    private const int MinimumSessions = 3;
    private const double MinimumRecentHours = 2;
    private const double NotableProbability = 0.5;

    public IEnumerable<Insight> Detect(InsightArchive archive, InsightPeriod period)
    {
        var abandonDays = archive.Options.AbandonDays;
        var byApp = archive.Sessions.GroupBy(session => session.AppId).Where(group => group.Count() >= MinimumSessions).ToList();
        if (byApp.Count == 0) yield break;

        var gaps = new List<(int Days, bool Open)>();
        foreach (var group in byApp)
        {
            var sessions = group.OrderBy(item => item.Start).ToList();
            for (var index = 1; index < sessions.Count; index++)
                gaps.Add(((int)((sessions[index].Start - sessions[index - 1].End) / 86400), false));
            gaps.Add(((int)((archive.Now - sessions[^1].End) / 86400), true));
        }

        var manual = archive.Statuses.Select(item => item.AppId).ToHashSet();
        var recentFrom = archive.Now - 90 * 86400L;
        var candidate = byApp
            .Where(group => !manual.Contains(group.Key))
            .Select(group => (
                AppId: group.Key,
                Name: group.First().Name,
                IdleDays: (int)((archive.Now - group.Max(item => item.End)) / 86400),
                RecentHours: InsightMath.Hours(group.Where(item => item.Start >= recentFrom).Sum(item => item.Seconds))))
            .Where(item => item.IdleDays >= MinimumIdleDays && item.IdleDays < abandonDays && item.RecentHours >= MinimumRecentHours)
            .OrderByDescending(item => item.RecentHours)
            .FirstOrDefault();
        if (candidate.Name == null) yield break;

        var known = gaps.Where(gap => gap.Days >= candidate.IdleDays && (!gap.Open || gap.Days >= abandonDays)).ToList();
        if (known.Count == 0) yield break;
        var probability = (double)known.Count(gap => gap.Days >= abandonDays) / known.Count;
        if (probability < NotableProbability) yield break;

        yield return new Insight(
            "forecast.abandonRisk",
            InsightKind.Forecast,
            period.FromDay,
            period.ToDay,
            new Dictionary<string, double> { ["probability"] = probability, ["idleDays"] = candidate.IdleDays, ["cases"] = known.Count },
            known.Count,
            InsightMath.Clamp(0.55 + known.Count / 200.0, 0, 0.9),
            [new InsightEvidence(InsightEvidenceLevel.Derived, "v_session")])
        {
            AppId = candidate.AppId,
            AppName = candidate.Name
        };
    }
}
