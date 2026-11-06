namespace SteamStat.Core.Insights;

/// <summary>洞察的四类来源，对应愿景文档第四节。</summary>
public enum InsightKind
{
    /// <summary>行为画像：作息漂移、挂机率、爆肝型或细水长流型。</summary>
    Behavior,
    /// <summary>因果叠加：把更新、特卖、节假日叠到时长曲线上。</summary>
    Causal,
    /// <summary>预测：积压库清算时间、全成就日期、弃坑概率。</summary>
    Forecast,
    /// <summary>异常与对账：时长异常、官方差异、记录断档。</summary>
    Anomaly
}

/// <summary>
/// 一条数字的可信程度。和「可解释的数字」、以后的时光机共用同一套标记：
/// recorded 实测，official Steam 官方数据，derived 由其他数字算出，unknown 无从知晓。
/// </summary>
public static class InsightEvidenceLevel
{
    public const string Recorded = "recorded";
    public const string Official = "official";
    public const string Derived = "derived";
    public const string Unknown = "unknown";
}

/// <summary>这条洞察依据的数据，点开时可以回到原始记录。</summary>
public sealed record InsightEvidence(string Level, string View, string? Filter = null);

/// <summary>
/// 洞察的统一结构。这里只有事实和数字，没有文案：周刊用模板把它写成报纸，
/// 配了 AI 时交给模型写，以后的图表批注直接读 Facts。
/// </summary>
public sealed record Insight(
    /// <summary>洞察类型，也是模板的键，例如 behavior.idleRate。</summary>
    string Id,
    InsightKind Kind,
    /// <summary>逻辑日闭区间。</summary>
    string FromDay,
    string ToDay,
    IReadOnlyDictionary<string, double> Facts,
    /// <summary>支撑这个结论的样本数（会话、天或游戏）。</summary>
    int SampleSize,
    /// <summary>0~1。不是统计学置信度，是检测器对「这不是巧合」的把握。</summary>
    double Confidence,
    IReadOnlyList<InsightEvidence> Evidence);

/// <summary>显著性门槛：样本不够的洞察不出现。宁可少说，不要在三局游戏上讲规律。</summary>
public sealed record InsightGate(int MinimumSamples = 10, double MinimumConfidence = 0.6)
{
    public bool Passes(Insight insight) => insight.SampleSize >= MinimumSamples && insight.Confidence >= MinimumConfidence;
}

public sealed record InsightPeriod(string FromDay, string ToDay);

/// <summary>检测器不知道数据从哪来，只经语义视图读取。</summary>
public interface IInsightDataSource
{
    Task<IReadOnlyList<object?[]>> QueryAsync(
        string sql,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken = default);
}

public interface IInsightDetector
{
    Task<IReadOnlyList<Insight>> DetectAsync(IInsightDataSource data, InsightPeriod period, CancellationToken cancellationToken = default);
}

public sealed class InsightEngine(IEnumerable<IInsightDetector> detectors, InsightGate? gate = null)
{
    private readonly InsightGate _gate = gate ?? new InsightGate();

    /// <summary>按把握从高到低排序，未过门槛的不返回。</summary>
    public async Task<IReadOnlyList<Insight>> RunAsync(IInsightDataSource data, InsightPeriod period, CancellationToken cancellationToken = default)
    {
        var insights = new List<Insight>();
        foreach (var detector in detectors)
            insights.AddRange(await detector.DetectAsync(data, period, cancellationToken).ConfigureAwait(false));
        return insights.Where(_gate.Passes).OrderByDescending(insight => insight.Confidence).ToList();
    }
}

/// <summary>第一个检测器：挂机率明显偏高。也是新检测器的写法示例。</summary>
public sealed class IdleRateDetector : IInsightDetector
{
    private const double NotableRate = 0.25;

    public async Task<IReadOnlyList<Insight>> DetectAsync(IInsightDataSource data, InsightPeriod period, CancellationToken cancellationToken = default)
    {
        var rows = await data.QueryAsync(
            """
            SELECT COUNT(DISTINCT session_id), SUM(idle_seconds), SUM(seconds)
            FROM v_session_day WHERE logical_day BETWEEN $from AND $to
            """,
            new Dictionary<string, object?> { ["$from"] = period.FromDay, ["$to"] = period.ToDay },
            cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0 || rows[0][2] == null) return [];

        var sessions = Convert.ToInt32(rows[0][0], System.Globalization.CultureInfo.InvariantCulture);
        var idle = Convert.ToDouble(rows[0][1], System.Globalization.CultureInfo.InvariantCulture);
        var total = Convert.ToDouble(rows[0][2], System.Globalization.CultureInfo.InvariantCulture);
        if (total <= 0) return [];
        var rate = idle / total;
        if (rate < NotableRate) return [];

        return
        [
            new Insight(
                "behavior.idleRate",
                InsightKind.Behavior,
                period.FromDay,
                period.ToDay,
                new Dictionary<string, double> { ["rate"] = rate, ["idleSeconds"] = idle, ["totalSeconds"] = total },
                sessions,
                Math.Min(1, 0.5 + sessions / 100.0),
                [new InsightEvidence(InsightEvidenceLevel.Recorded, "v_session_day", $"logical_day BETWEEN '{period.FromDay}' AND '{period.ToDay}'")])
        ];
    }
}
