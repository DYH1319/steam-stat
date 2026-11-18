using SteamStat.Core.Insights;

namespace SteamStat.Core.Circle;

public sealed record CompatibilityInput(
    /// <summary>最近一年我玩游戏的周作息（秒）。</summary>
    IReadOnlyList<double> MySchedule,
    /// <summary>同一段时间里观测到的对方玩游戏的周作息（秒）。</summary>
    IReadOnlyList<double> TheirSchedule,
    IReadOnlyDictionary<int, int> MyMinutes,
    IReadOnlyDictionary<int, int> TheirMinutes,
    IReadOnlyDictionary<int, InsightAppFacts> Apps,
    long TogetherSeconds);

public sealed record CompatibilityScore(
    /// <summary>0..1，能算的分项不到两项时为空。</summary>
    double? Overall,
    double? Schedule,
    double? Taste,
    double? Together,
    double MyHours,
    double TheirHours,
    int MyGames,
    int TheirGames,
    int SharedGames,
    IReadOnlyList<int> TopShared,
    /// <summary>两人都常玩的时段（星期 × 24 + 小时），算不出时为空。</summary>
    int? BestCell);

/// <summary>
/// 好友契合度：作息、口味、共同时长三项，每项都有样本门槛，样本不够的那项不算，不拿零分凑数。
/// </summary>
public static class Compatibility
{
    public const double MinScheduleHours = 10;
    public const int MinTasteGames = 5;
    /// <summary>一起玩 20 小时约等于 0.63 分，之后增长放缓，几百小时的老搭档不会把其他两项压没。</summary>
    public const double TogetherHalfLifeHours = 20;
    private const double ScheduleWeight = 0.35;
    private const double TasteWeight = 0.35;
    private const double TogetherWeight = 0.3;

    public static CompatibilityScore Score(CompatibilityInput input)
    {
        var myHours = input.MySchedule.Sum() / 3600;
        var theirHours = input.TheirSchedule.Sum() / 3600;
        double? schedule = myHours >= MinScheduleHours && theirHours >= MinScheduleHours ? WeekHours.Cosine(input.MySchedule, input.TheirSchedule) : null;

        var mine = input.MyMinutes.Where(pair => pair.Value > 0).ToDictionary(pair => pair.Key, pair => pair.Value);
        var theirs = input.TheirMinutes.Where(pair => pair.Value > 0).ToDictionary(pair => pair.Key, pair => pair.Value);
        var shared = mine.Keys.Intersect(theirs.Keys).ToList();
        double? taste = null;
        if (mine.Count >= MinTasteGames && theirs.Count >= MinTasteGames)
        {
            var jaccard = WeightedJaccard(mine, theirs);
            var tags = WeekHours.Cosine(TagVector(mine, input.Apps), TagVector(theirs, input.Apps));
            taste = tags is { } tagScore ? 0.6 * jaccard + 0.4 * tagScore : jaccard;
        }

        double? together = input.TogetherSeconds > 0 ? 1 - Math.Exp(-input.TogetherSeconds / 3600.0 / TogetherHalfLifeHours) : null;

        var parts = new List<(double Value, double Weight)>();
        if (schedule is { } a) parts.Add((a, ScheduleWeight));
        if (taste is { } b) parts.Add((b, TasteWeight));
        if (together is { } c) parts.Add((c, TogetherWeight));
        double? overall = parts.Count >= 2 ? parts.Sum(part => part.Value * part.Weight) / parts.Sum(part => part.Weight) : null;

        int? best = null;
        if (schedule != null)
        {
            var myTotal = input.MySchedule.Sum();
            var theirTotal = input.TheirSchedule.Sum();
            var score = Enumerable.Range(0, WeekHours.Cells)
                .Select(index => (Index: index, Value: Math.Min(input.MySchedule[index] / myTotal, input.TheirSchedule[index] / theirTotal)))
                .MaxBy(item => item.Value);
            if (score.Value > 0) best = score.Index;
        }

        return new CompatibilityScore(
            overall, schedule, taste, together, Math.Round(myHours, 1), Math.Round(theirHours, 1), mine.Count, theirs.Count, shared.Count,
            shared.OrderByDescending(appId => Math.Min(mine[appId], theirs[appId])).Take(5).ToList(),
            best);
    }

    /// <summary>按 log(1 + 分钟) 加权：玩了 500 小时和 50 小时的差别没有 5 小时和 0 小时大。</summary>
    internal static double WeightedJaccard(IReadOnlyDictionary<int, int> left, IReadOnlyDictionary<int, int> right)
    {
        double min = 0, max = 0;
        foreach (var appId in left.Keys.Union(right.Keys))
        {
            var a = Math.Log(1 + left.GetValueOrDefault(appId));
            var b = Math.Log(1 + right.GetValueOrDefault(appId));
            min += Math.Min(a, b);
            max += Math.Max(a, b);
        }
        return max <= 0 ? 0 : min / max;
    }

    private static double[] TagVector(IReadOnlyDictionary<int, int> minutes, IReadOnlyDictionary<int, InsightAppFacts> apps)
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var facts in apps.Values)
            foreach (var tag in facts.Tags.Take(10))
                index.TryAdd(tag, index.Count);
        var vector = new double[index.Count];
        foreach (var (appId, value) in minutes)
        {
            if (!apps.TryGetValue(appId, out var facts)) continue;
            // 靠前的标签票数多，更能代表这款游戏
            var tags = facts.Tags.Take(10).ToList();
            for (var position = 0; position < tags.Count; position++)
                vector[index[tags[position]]] += Math.Log(1 + value) * (1.0 - position * 0.07);
        }
        return vector;
    }
}

/// <summary>
/// 好友在每个时段在线的概率：在线秒数 ÷ 应用在观测的秒数。应用没开着的时候不知道对方在不在线，所以不能除以总时长。
/// </summary>
public static class PresenceForecast
{
    /// <summary>一个时段累计观测不到这么久时不给概率。</summary>
    public const double MinObservedSeconds = 1800;

    /// <returns>168 个格子，不知道的格子是 null。</returns>
    public static double?[] Build(IEnumerable<Span> online, IEnumerable<Span> observed, InsightOptions options, long from, long to)
    {
        var watched = Spans.Union(observed.Select(span => new Span(Math.Max(span.Start, from), Math.Min(span.End, to))).Where(span => span.End > span.Start));
        var seen = Spans.Intersect(online.Select(span => new Span(Math.Max(span.Start, from), Math.Min(span.End, to))).Where(span => span.End > span.Start), watched);
        var numerator = WeekHours.From(seen, options);
        var denominator = WeekHours.From(watched, options);
        var output = new double?[WeekHours.Cells];
        for (var index = 0; index < WeekHours.Cells; index++)
            output[index] = denominator[index] >= MinObservedSeconds ? Math.Clamp(numerator[index] / denominator[index], 0, 1) : null;
        return output;
    }
}
