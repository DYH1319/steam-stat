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
    IReadOnlyList<InsightEvidence> Evidence)
{
    private static readonly IReadOnlyDictionary<string, string> NoLabels = new Dictionary<string, string>();

    /// <summary>不是数字的事实：回坑的原因、节日名、标签名。</summary>
    public IReadOnlyDictionary<string, string> Labels { get; init; } = NoLabels;

    /// <summary>这条洞察说的是哪款游戏。</summary>
    public int? AppId { get; init; }

    public string? AppName { get; init; }

    /// <summary>
    /// 这是一件发生过的事，不是从样本里归纳的规律，所以不看样本数，只看把握。
    /// 「时隔 90 天回到某游戏」不需要十局游戏来证明。
    /// </summary>
    public bool IsEvent { get; init; }
}

/// <summary>显著性门槛：样本不够的洞察不出现。宁可少说，不要在三局游戏上讲规律。</summary>
public sealed record InsightGate(int MinimumSamples = 10, double MinimumConfidence = 0.6)
{
    public bool Passes(Insight insight)
        => (insight.IsEvent || insight.SampleSize >= MinimumSamples) && insight.Confidence >= MinimumConfidence;
}

public sealed record InsightPeriod(string FromDay, string ToDay);

/// <summary>读取语义视图。洞察只经这里拿数据，隐藏游戏和口径设置自动生效。</summary>
public interface IInsightDataSource
{
    Task<IReadOnlyList<object?[]>> QueryAsync(
        string sql,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken = default);
}

/// <summary>检测器是纯函数：只看已经读进内存的档案，所以十几个检测器不用各自跑一遍视图。</summary>
public interface IInsightDetector
{
    IEnumerable<Insight> Detect(InsightArchive archive, InsightPeriod period);
}

public sealed class InsightEngine(IEnumerable<IInsightDetector> detectors, InsightGate? gate = null)
{
    private readonly InsightGate _gate = gate ?? new InsightGate();

    /// <summary>全部内置检测器。</summary>
    public static IReadOnlyList<IInsightDetector> DefaultDetectors { get; } =
    [
        new IdleRateDetector(),
        new ScheduleDriftDetector(),
        new DroppedGenreDetector(),
        new PlayStyleDetector(),
        new ComebackDetector(),
        new BacklogClearanceDetector(),
        new CompletionDateDetector(),
        new AbandonRiskDetector(),
        new PlaytimeSpikeDetector(),
        new OfficialDiffDetector(),
        new RecordGapDetector()
    ];

    public static InsightEngine CreateDefault(InsightGate? gate = null) => new(DefaultDetectors, gate);

    /// <summary>按把握从高到低排序，未过门槛的不返回。</summary>
    public IReadOnlyList<Insight> Run(InsightArchive archive, InsightPeriod period)
        => detectors
            .SelectMany(detector => detector.Detect(archive, period))
            .Where(_gate.Passes)
            .OrderByDescending(insight => insight.Confidence)
            .ThenBy(insight => insight.Id, StringComparer.Ordinal)
            .ToList();
}
