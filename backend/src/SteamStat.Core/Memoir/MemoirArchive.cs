using System.Globalization;
using SteamStat.Core.Insights;

namespace SteamStat.Core.Memoir;

/// <summary>
/// 证据分级：能证实的正常显示，推算的带「推算」标记，无从知晓的灰色占位。
/// 顺序就是强弱，合并两个数字时取较弱的那个。
/// </summary>
public enum MemoirEvidence
{
    Recorded = 0,
    Official = 1,
    Inferred = 2,
    Unknown = 3
}

public static class MemoirEvidences
{
    public static string Id(this MemoirEvidence evidence) => evidence switch
    {
        MemoirEvidence.Recorded => "recorded",
        MemoirEvidence.Official => "official",
        MemoirEvidence.Inferred => "inferred",
        _ => "unknown"
    };

    public static MemoirEvidence Weakest(MemoirEvidence left, MemoirEvidence right) => (MemoirEvidence)Math.Max((int)left, (int)right);
}

/// <summary>带证据的数字。推算或未知时 <see cref="Upper"/> 是已知的上限。</summary>
public sealed record MemoirValue(long Value, MemoirEvidence Evidence, long? Upper = null)
{
    public static MemoirValue Zero(MemoirEvidence evidence) => new(0, evidence);

    public static MemoirValue Unknown(long? upper = null) => new(0, MemoirEvidence.Unknown, upper);

    public MemoirValue Plus(MemoirValue other)
    {
        // 未知加上已知还是未知，但已知部分要留着，界面显示「至少」
        var evidence = MemoirEvidences.Weakest(Evidence, other.Evidence);
        long? upper = Upper == null && other.Upper == null ? null : (Upper ?? Value) + (other.Upper ?? other.Value);
        return new MemoirValue(Value + other.Value, evidence, upper);
    }
}

/// <summary>日期换算。渲染层和 Host 之间传「距 1970-01-01 的天数」，不传日期字符串。</summary>
public static class MemoirDays
{
    private static readonly int EpochDayNumber = new DateOnly(1970, 1, 1).DayNumber;

    public static DateOnly FromEpoch(int days) => DateOnly.FromDayNumber(EpochDayNumber + days);

    public static int ToEpoch(DateOnly date) => date.DayNumber - EpochDayNumber;

    public static int ToEpoch(string day) => ToEpoch(InsightDays.Parse(day));

    public static string Format(int days) => InsightDays.Format(FromEpoch(days));

    public static string Month(DateOnly date) => date.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public static string Month(string day) => day[..7];

    public static DateOnly MonthStart(string month) => DateOnly.ParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>逻辑日开始的 Unix 秒（含分界点偏移）。</summary>
    public static long StartOf(InsightOptions options, DateOnly date)
    {
        var local = date.ToDateTime(new TimeOnly(0, 0)).AddHours(options.DayBoundaryHour);
        return new DateTimeOffset(local, options.TimeZone.GetUtcOffset(local)).ToUnixTimeSeconds();
    }

    /// <summary>逻辑日结束（不含）的 Unix 秒。</summary>
    public static long EndOf(InsightOptions options, DateOnly date) => StartOf(options, date.AddDays(1));

    public static IEnumerable<string> MonthsBetween(string fromMonth, string toMonth)
    {
        var cursor = MonthStart(fromMonth);
        var end = MonthStart(toMonth);
        while (cursor <= end)
        {
            yield return Month(cursor);
            cursor = cursor.AddMonths(1);
        }
    }
}

/// <summary>官方累计时长的一段平台期（playtime_snapshot）：[FirstObservedAt, LastObservedAt] 内值不变。</summary>
public sealed record MemoirOfficialPlaytime(string SteamId, int AppId, int Minutes, long FirstObservedAt, long LastObservedAt);

/// <summary>Steam 记录的首次游玩时间，来自 ClientGetLastPlayedTimes 或年度回顾。</summary>
public sealed record MemoirFirstPlay(string SteamId, int AppId, long FirstPlayed);

public enum MemoirTraceKind
{
    Achievement,
    Screenshot,
    CloudSave,
    FirstPlay
}

public static class MemoirTraceKinds
{
    public static string Id(this MemoirTraceKind kind) => kind switch
    {
        MemoirTraceKind.Achievement => "achievement",
        MemoirTraceKind.Screenshot => "screenshot",
        MemoirTraceKind.CloudSave => "cloudSave",
        _ => "firstPlay"
    };
}

/// <summary>游玩留下的痕迹：成就解锁、截图、云存档写入、官方首玩。这一天大概率玩过，但不知道玩了多久。</summary>
public sealed record MemoirTrace(string SteamId, int AppId, MemoirTraceKind Kind, long Time);

/// <summary>现在的好友（来自好友快照）。</summary>
public sealed record MemoirFriend(string OwnerSteamId, string SteamId, string Name);

/// <summary>好友列表的增删和改名，只在 Steam Stat 运行并登录时观测得到。</summary>
public sealed record MemoirFriendChange(string OwnerSteamId, string SteamId, string Name, string Kind, string? From, string? To, long Time);

public sealed record MemoirProfileChange(string SteamId, string Field, string Value, long ObservedAt);

public sealed record MemoirLifeEvent(int Id, string Title, string? Note, string Kind, int StartDay, int? EndDay, bool Boundary, long UpdatedAt)
{
    public bool Covers(int day) => day >= StartDay && day <= (EndDay ?? StartDay);
}

/// <summary>
/// 回忆录读进内存的档案：洞察档案（会话、成就、游戏库，已经剔除了隐藏游戏）加上史前考古的证据。
/// 所有算法都只看这里，所以都是纯函数。
/// </summary>
public sealed class MemoirArchive
{
    public required InsightArchive Insight { get; init; }
    public IReadOnlyList<MemoirOfficialPlaytime> Official { get; init; } = [];
    public IReadOnlyList<MemoirFirstPlay> FirstPlays { get; init; } = [];
    public IReadOnlyList<MemoirTrace> Traces { get; init; } = [];
    public IReadOnlyList<YearReview> YearReviews { get; init; } = [];
    public IReadOnlyList<MemoirFriend> Friends { get; init; } = [];
    public IReadOnlyList<MemoirFriendChange> FriendChanges { get; init; } = [];
    /// <summary>每个账号最早的好友记录，在这之前好友列表的变化不知道。</summary>
    public IReadOnlyDictionary<string, long> FriendsKnownSince { get; init; } = new Dictionary<string, long>();
    public IReadOnlyList<MemoirProfileChange> Profile { get; init; } = [];
    public IReadOnlyList<MemoirLifeEvent> LifeEvents { get; init; } = [];
    public IReadOnlyList<MemoirAcquisition> Acquisitions { get; init; } = [];
    /// <summary>游戏名：会话、游戏库、成就里出现过的。</summary>
    public IReadOnlyDictionary<int, string> Names { get; init; } = new Dictionary<int, string>();

    public InsightOptions Options => Insight.Options;

    public long Now => Insight.Now;

    /// <summary>第一局记录的开始时间。在这之前是史前，0 表示还没有记录。</summary>
    public long TrackingStart => Insight.Sessions.Count > 0 ? Insight.Sessions[0].Start : 0;

    public string NameOf(int appId) => Names.TryGetValue(appId, out var name) && !string.IsNullOrWhiteSpace(name)
        ? name
        : string.Create(CultureInfo.InvariantCulture, $"App {appId}");

    /// <summary>最早的证据：官方首玩、痕迹、记录、年度回顾里最早的那个。</summary>
    public long? FirstEvidence()
    {
        var times = new List<long>();
        if (TrackingStart > 0) times.Add(TrackingStart);
        times.AddRange(FirstPlays.Select(item => item.FirstPlayed).Where(time => time > 0));
        times.AddRange(Traces.Select(item => item.Time).Where(time => time > 0));
        if (YearReviews.Count > 0)
            times.Add(MemoirDays.StartOf(Options, new DateOnly(YearReviews.Min(review => review.Year), 1, 1)));
        return times.Count == 0 ? null : times.Min();
    }
}
