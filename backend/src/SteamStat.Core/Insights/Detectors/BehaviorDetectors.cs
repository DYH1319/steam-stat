namespace SteamStat.Core.Insights;

/// <summary>挂机率明显偏高。</summary>
public sealed class IdleRateDetector : IInsightDetector
{
    private const double NotableRate = 0.25;

    public IEnumerable<Insight> Detect(InsightArchive archive, InsightPeriod period)
    {
        var sessions = archive.SessionsIn(period).ToList();
        var total = sessions.Sum(session => session.Seconds);
        if (total <= 0) yield break;
        var idle = sessions.Sum(session => session.IdleSeconds);
        var rate = (double)idle / total;
        if (rate < NotableRate) yield break;
        yield return new Insight(
            "behavior.idleRate",
            InsightKind.Behavior,
            period.FromDay,
            period.ToDay,
            new Dictionary<string, double> { ["rate"] = rate, ["idleSeconds"] = idle, ["totalSeconds"] = total },
            sessions.Count,
            Math.Min(1, 0.5 + sessions.Count / 100.0),
            [new InsightEvidence(InsightEvidenceLevel.Recorded, "v_session", $"logical_day BETWEEN '{period.FromDay}' AND '{period.ToDay}'")]);
    }
}

/// <summary>作息漂移：开局时刻比前一段时间明显提前或推迟。用循环均值，23 点和 1 点的平均是 0 点。</summary>
public sealed class ScheduleDriftDetector : IInsightDetector
{
    private const double NotableShiftHours = 1;
    private const double MinimumConcentration = 0.3;
    private const int BaselinePeriods = 4;

    public IEnumerable<Insight> Detect(InsightArchive archive, InsightPeriod period)
    {
        var length = InsightDays.Between(period.FromDay, period.ToDay) + 1;
        var baselineFrom = InsightDays.Shift(period.FromDay, -length * BaselinePeriods);
        var baselineTo = InsightDays.Shift(period.FromDay, -1);
        var current = HoursOf(archive, period.FromDay, period.ToDay);
        var baseline = HoursOf(archive, baselineFrom, baselineTo);
        if (current.Count < 5 || baseline.Count < 10) yield break;

        var currentMean = InsightMath.CircularMeanHour(current, out var currentConcentration);
        var baselineMean = InsightMath.CircularMeanHour(baseline, out var baselineConcentration);
        if (currentConcentration < MinimumConcentration || baselineConcentration < MinimumConcentration) yield break;
        var shift = InsightMath.HourDifference(baselineMean, currentMean);
        if (Math.Abs(shift) < NotableShiftHours) yield break;

        yield return new Insight(
            "behavior.scheduleDrift",
            InsightKind.Behavior,
            period.FromDay,
            period.ToDay,
            new Dictionary<string, double> { ["currentHour"] = currentMean, ["baselineHour"] = baselineMean, ["shiftHours"] = shift },
            current.Count + baseline.Count,
            InsightMath.Clamp(0.5 + Math.Abs(shift) * 0.08 + Math.Min(0.2, (current.Count + baseline.Count) / 200.0), 0, 0.95),
            [new InsightEvidence(InsightEvidenceLevel.Derived, "v_session", $"logical_day BETWEEN '{baselineFrom}' AND '{period.ToDay}'")]);
    }

    private static List<double> HoursOf(InsightArchive archive, string from, string to)
        => archive.SessionsIn(from, to).Select(session =>
        {
            var local = archive.Options.Local(session.Start);
            return local.Hour + local.Minute / 60.0;
        }).ToList();
}

/// <summary>入坑即弃的类型：哪类游戏买了玩两下就放下了，比整体水平明显更高才值得说。</summary>
public sealed class DroppedGenreDetector : IInsightDetector
{
    private const double DroppedHours = 2;
    private const double NotableRate = 0.5;
    private const double NotableMargin = 0.15;
    private const int MinimumGames = 10;

    /// <summary>几乎所有游戏都有的标签，说了等于没说。</summary>
    private static readonly HashSet<string> TooBroad = new(StringComparer.OrdinalIgnoreCase)
    {
        "Singleplayer", "Multiplayer", "Indie", "Action", "Adventure", "Casual", "2D", "3D", "Atmospheric",
        "Great Soundtrack", "Story Rich", "Early Access", "Free to Play", "Co-op"
    };

    public IEnumerable<Insight> Detect(InsightArchive archive, InsightPeriod period)
    {
        var settled = archive.Now - archive.Options.AbandonDays * 86400L;
        var status = archive.Statuses.ToLookup(item => item.AppId);
        var games = archive.Sessions
            .GroupBy(session => session.AppId)
            .Select(group => (AppId: group.Key, First: group.Min(item => item.Start), Last: group.Max(item => item.End), Seconds: group.Sum(item => item.Seconds)))
            // 刚入坑的游戏还看不出结果，手动标记过的不用猜
            .Where(game => game.First <= settled && !status[game.AppId].Any(item => item.Status is "completed" or "playing" or "wantToPlay"))
            .ToList();
        if (games.Count < MinimumGames) yield break;

        bool Dropped((int AppId, long First, long Last, long Seconds) game)
            => InsightMath.Hours(game.Seconds) < DroppedHours && game.Last <= settled;

        var overall = (double)games.Count(Dropped) / games.Count;
        var byTag = new Dictionary<string, (int Games, int Dropped)>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in games)
        {
            var facts = archive.FactsOf(game.AppId);
            if (facts == null) continue;
            foreach (var tag in facts.Genres.Concat(facts.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Where(tag => !TooBroad.Contains(tag)))
            {
                var value = byTag.GetValueOrDefault(tag);
                byTag[tag] = (value.Games + 1, value.Dropped + (Dropped(game) ? 1 : 0));
            }
        }

        var best = byTag
            .Where(item => item.Value.Games >= MinimumGames)
            .Select(item => (Tag: item.Key, item.Value.Games, item.Value.Dropped, Rate: (double)item.Value.Dropped / item.Value.Games))
            .Where(item => item.Rate >= NotableRate && item.Rate >= overall + NotableMargin)
            .OrderByDescending(item => item.Rate)
            .ThenBy(item => item.Tag, StringComparer.Ordinal)
            .FirstOrDefault();
        if (best.Tag == null) yield break;

        yield return new Insight(
            "behavior.droppedGenre",
            InsightKind.Behavior,
            period.FromDay,
            period.ToDay,
            new Dictionary<string, double> { ["rate"] = best.Rate, ["overallRate"] = overall, ["games"] = best.Games, ["dropped"] = best.Dropped },
            best.Games,
            InsightMath.Clamp(0.55 + (best.Rate - overall) * 0.8 + Math.Min(0.1, best.Games / 200.0), 0, 0.95),
            [new InsightEvidence(InsightEvidenceLevel.Derived, "v_app_playtime")])
        {
            Labels = new Dictionary<string, string> { ["tag"] = best.Tag }
        };
    }
}

/// <summary>爆肝型或细水长流型：一款游戏的时长有多少集中在最忙的七天里，按时长加权。</summary>
public sealed class PlayStyleDetector : IInsightDetector
{
    private const double MinimumHours = 5;
    private const double BingeShare = 0.6;
    private const double SteadyShare = 0.3;

    public IEnumerable<Insight> Detect(InsightArchive archive, InsightPeriod period)
    {
        var from = InsightDays.Shift(period.ToDay, -364);
        var games = archive.SessionsIn(from, period.ToDay)
            .GroupBy(session => session.AppId)
            .Select(group => (Seconds: group.Sum(item => item.Seconds), Busiest: BusiestWeek(group)))
            .Where(game => InsightMath.Hours(game.Seconds) >= MinimumHours)
            .ToList();
        if (games.Count == 0) yield break;
        var total = games.Sum(game => (double)game.Seconds);
        var concentration = games.Sum(game => (double)game.Busiest) / total;
        var style = concentration >= BingeShare ? "binge" : concentration <= SteadyShare ? "steady" : null;
        if (style == null) yield break;

        yield return new Insight(
            "behavior.playStyle",
            InsightKind.Behavior,
            from,
            period.ToDay,
            new Dictionary<string, double> { ["concentration"] = concentration, ["games"] = games.Count },
            games.Count,
            InsightMath.Clamp(0.55 + Math.Min(0.25, games.Count / 60.0) + Math.Abs(concentration - 0.45) * 0.3, 0, 0.95),
            [new InsightEvidence(InsightEvidenceLevel.Derived, "v_session", $"logical_day BETWEEN '{from}' AND '{period.ToDay}'")])
        {
            Labels = new Dictionary<string, string> { ["style"] = style }
        };
    }

    private static long BusiestWeek(IEnumerable<InsightSession> sessions)
    {
        var days = sessions
            .GroupBy(session => InsightDays.Parse(session.Day).DayNumber)
            .OrderBy(group => group.Key)
            .Select(group => (Day: group.Key, Seconds: group.Sum(item => item.Seconds)))
            .ToList();
        long best = 0, window = 0;
        var start = 0;
        foreach (var (day, seconds) in days)
        {
            window += seconds;
            while (day - days[start].Day >= 7)
            {
                window -= days[start].Seconds;
                start++;
            }
            best = Math.Max(best, window);
        }
        return best;
    }
}

/// <summary>回坑和它前后发生的事。没有任何线索的回坑把握很低，不算洞察，但周刊的故事里还会用。</summary>
public sealed class ComebackDetector : IInsightDetector
{
    public IEnumerable<Insight> Detect(InsightArchive archive, InsightPeriod period)
    {
        foreach (var comeback in ComebackAnalysis.Find(archive, period.FromDay, period.ToDay))
        {
            var cause = comeback.MainCause;
            var labels = new Dictionary<string, string> { ["cause"] = cause?.Kind ?? "unknown" };
            if (cause?.Name != null)
            {
                labels["causeZh"] = cause.Name.Zh;
                labels["causeEn"] = cause.Name.En;
            }
            var facts = new Dictionary<string, double> { ["gapDays"] = comeback.GapDays, ["seconds"] = comeback.SecondsAfter };
            if (cause?.DaysBefore != null) facts["causeDaysBefore"] = cause.DaysBefore.Value;
            yield return new Insight(
                "causal.comeback",
                InsightKind.Causal,
                comeback.Day,
                period.ToDay,
                facts,
                1,
                cause?.Strength ?? 0.4,
                [new InsightEvidence(InsightEvidenceLevel.Derived, "v_session", $"app_id = {comeback.AppId}")])
            {
                Labels = labels,
                AppId = comeback.AppId,
                AppName = comeback.Name,
                IsEvent = true
            };
        }
    }
}
