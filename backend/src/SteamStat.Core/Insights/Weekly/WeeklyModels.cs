namespace SteamStat.Core.Insights;

/// <summary>
/// 一期周刊的全部内容。生成后序列化存进数据库，之后只读：过刊馆保存的是当时的文字和数字。
/// 版面上的栏目名是界面文案，不在这里。新增字段时旧期号没有这个字段，渲染层要能处理。
/// </summary>
public sealed record WeeklyPayload
{
    public const int CurrentFormat = 1;

    public int FormatVersion { get; init; } = CurrentFormat;
    /// <summary>weekly 周刊，extra 号外。</summary>
    public required string Kind { get; init; }
    public required int Number { get; init; }
    public required string FromDay { get; init; }
    public required string ToDay { get; init; }
    public required long GeneratedAt { get; init; }
    /// <summary>补印：不是当时生成的。</summary>
    public bool Backfilled { get; init; }
    /// <summary>这一期没有任何游戏活动。</summary>
    public bool Quiet { get; init; }
    public required WeeklyStory Headline { get; init; }
    public IReadOnlyList<WeeklyStory> Briefs { get; init; } = [];
    public IReadOnlyList<Bilingual> Observations { get; init; } = [];
    public required WeeklyNumbers Numbers { get; init; }
    public WeeklySchedule? Schedule { get; init; }
    public WeeklyFriends? Friends { get; init; }
    public IReadOnlyList<WeeklyObituary> Obituaries { get; init; } = [];
    public WeeklyMarket? Market { get; init; }
    public WeeklyForecast? Forecast { get; init; }
    public IReadOnlyList<WeeklyAd> Classifieds { get; init; } = [];
}

public sealed record WeeklyStory(string Id, Bilingual Title, Bilingual Body, int? AppId = null);

public sealed record WeeklyNumbers(
    long Seconds,
    long EffectiveSeconds,
    int Sessions,
    int Games,
    int ActiveDays,
    int Achievements,
    int NewGames,
    int LateNight,
    long PreviousSeconds);

/// <summary>Heat 是 7 × 24 的分钟数，行按一周起始日对齐，列是本地小时。</summary>
public sealed record WeeklySchedule(Bilingual Text, IReadOnlyList<IReadOnlyList<int>> Heat, int LateNight, double? LatestHour, double WeekendShare);

public sealed record WeeklyFriends(IReadOnlyList<Bilingual> Items);

public sealed record WeeklyObituary(int AppId, string Name, double Hours, int IdleDays, Bilingual Text);

/// <summary>一根周 K 线。价格是当天往前七天的时长（小时），成交量是这一周的时长。</summary>
public sealed record WeeklyCandle(string Week, double Open, double High, double Low, double Close, double Volume);

public sealed record WeeklyStock(int AppId, string Name, double Volume, double Previous, double? Change);

public sealed record WeeklyMarket(Bilingual Commentary, double Index, double? IndexChange, IReadOnlyList<WeeklyCandle> Candles, IReadOnlyList<WeeklyStock> Gainers, IReadOnlyList<WeeklyStock> Losers);

/// <summary>Sky 是 clear / cloudy / showers / calm，Part 是 morning / afternoon / evening / night。</summary>
public sealed record WeeklyForecastDay(string Day, string Sky, double Probability, string Part, double ExpectedHours, Bilingual Text);

public sealed record WeeklyForecast(Bilingual Summary, IReadOnlyList<WeeklyForecastDay> Days, Bilingual? Friend, int WeeksObserved);

public sealed record WeeklyAd(int AppId, string Name, Bilingual Text);

/// <summary>号外的触发事件。Key 是去重键，同一件事只出一次。</summary>
public sealed record WeeklyExtraEvent(string Key, string Kind, long Time, int? AppId, string? Name, double Value, double Previous = 0);
