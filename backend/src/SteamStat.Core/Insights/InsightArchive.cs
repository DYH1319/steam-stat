using System.Globalization;

namespace SteamStat.Core.Insights;

/// <summary>洞察用到的口径。这些都来自 Host 设置，和语义视图、渲染层图表一致。</summary>
public sealed record InsightOptions(
    int DayBoundaryHour = 4,
    int WeekStart = 1,
    int PauseDays = 30,
    int AbandonDays = 90,
    TimeZoneInfo? Zone = null,
    /// <summary>zh 或 en。只决定节假日用哪张表，文案始终两种语言都写。</summary>
    string Language = "zh")
{
    public TimeZoneInfo TimeZone => Zone ?? TimeZoneInfo.Local;

    public DateTimeOffset Local(long unixSeconds)
        => TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(unixSeconds), TimeZone);

    public DateOnly LogicalDate(long unixSeconds)
        => DateOnly.FromDateTime(Local(unixSeconds - DayBoundaryHour * 3600L).DateTime);

    public string DayOf(long unixSeconds) => InsightDays.Format(LogicalDate(unixSeconds));

    /// <summary>这个日期所在周的第一天。</summary>
    public DateOnly WeekOf(DateOnly date)
        => date.AddDays(-(((int)date.DayOfWeek - WeekStart + 7) % 7));

    /// <summary>最近一个已经结束的完整周。</summary>
    public InsightPeriod LastFullWeek(long now)
    {
        var start = WeekOf(LogicalDate(now)).AddDays(-7);
        return new InsightPeriod(InsightDays.Format(start), InsightDays.Format(start.AddDays(6)));
    }
}

public static class InsightDays
{
    public static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateOnly Parse(string day) => DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Shift(string day, int days) => Format(Parse(day).AddDays(days));

    public static int Between(string from, string to) => Parse(to).DayNumber - Parse(from).DayNumber;
}

/// <summary>清洗后的一局游戏，来自 v_session。</summary>
public sealed record InsightSession(
    long Id,
    string SteamId,
    int AppId,
    string Name,
    long Start,
    long End,
    long Seconds,
    long IdleSeconds,
    string Day,
    SessionTags Tags,
    int? BuildId);

public sealed record InsightUnlock(int AppId, string InternalName, string Name, double? GlobalPercent, long Time, string Day);

/// <summary>一款游戏的成就进度。<paramref name="Total"/> 是 Steam 口径。</summary>
public sealed record InsightProgress(int AppId, string Name, int Total, int Unlocked);

public sealed record InsightSetCompletion(int AppId, long GroupId, string Name, long CompletedAt);

public sealed record InsightOwnedGame(int AppId, string Name, int PlaytimeMinutes, long LastPlayed);

/// <summary>用户手动标记的状态，永远优先于推断。</summary>
public sealed record InsightGameStatus(int AppId, string Status, long UpdatedAt, long? CompletedAt, double? Price, string? Currency);

public sealed record InsightAcquisition(int AppId, long AcquiredAt);

/// <summary>本机观测到的游戏版本变化。第一次看到一款游戏只是基线，不算更新。</summary>
public sealed record InsightBuildChange(int AppId, int BuildId, long ObservedAt, bool IsBaseline);

public sealed record InsightSchemaChange(int AppId, long DetectedAt);

/// <summary>哪些 DLC 属于哪款游戏。Steam 只在成就集里给出这个关系，所以只覆盖有 DLC 成就集的游戏。</summary>
public sealed record InsightDlc(int AppId, int DlcAppId);

/// <summary>一款游戏的公开资料：商店状态优先，公共数据补充。</summary>
public sealed record InsightAppFacts(int AppId)
{
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<string> Genres { get; init; } = [];
    public string? Developer { get; init; }
    public string? Franchise { get; init; }
    /// <summary>Unix 秒，不知道时为空。</summary>
    public long? ReleaseDate { get; init; }
    /// <summary>美区现价（美分）。</summary>
    public int? PriceCents { get; init; }
    public bool? IsFree { get; init; }
    public bool Delisted { get; init; }
    public long? DelistedAt { get; init; }
    public int? ReviewPercent { get; init; }
    public int? ReviewCount { get; init; }
    public long? Owners { get; init; }
}

/// <summary>断档和官方差异由 Host 现有的对账算好，这里不再算一遍。</summary>
public sealed record InsightReconciliation(int Games, int Untracked, int Overcounted, IReadOnlyList<InsightGap> Gaps);

public sealed record InsightGap(string FromDay, string ToDay, long UntrackedSeconds);

/// <summary>
/// 读进内存的档案。检测器和周刊都只看这里，读一次视图可以算完全部洞察。
/// 十年档案是五万局，放进内存没有压力；好友记录按时间窗读，见 <see cref="InsightFriendData"/>。
/// </summary>
public sealed class InsightArchive
{
    public required long Now { get; init; }
    public required InsightOptions Options { get; init; }
    /// <summary>按开始时间升序。</summary>
    public IReadOnlyList<InsightSession> Sessions { get; init; } = [];
    public IReadOnlyList<InsightUnlock> Unlocks { get; init; } = [];
    public IReadOnlyList<InsightProgress> Progress { get; init; } = [];
    public IReadOnlyList<InsightSetCompletion> SetCompletions { get; init; } = [];
    public IReadOnlyList<InsightOwnedGame> Owned { get; init; } = [];
    public IReadOnlyList<InsightGameStatus> Statuses { get; init; } = [];
    public IReadOnlyList<InsightAcquisition> Acquisitions { get; init; } = [];
    public IReadOnlyList<InsightBuildChange> Builds { get; init; } = [];
    public IReadOnlyList<InsightSchemaChange> SchemaChanges { get; init; } = [];
    public IReadOnlyList<InsightDlc> Dlcs { get; init; } = [];
    public IReadOnlyList<CrashBurst> Crashes { get; init; } = [];
    public IReadOnlyDictionary<int, InsightAppFacts> Apps { get; init; } = new Dictionary<int, InsightAppFacts>();
    public InsightFriendData Friends { get; init; } = InsightFriendData.Empty;
    public InsightReconciliation? Reconciliation { get; init; }

    public InsightAppFacts? FactsOf(int appId) => Apps.GetValueOrDefault(appId);

    public IReadOnlySet<int> DlcOf(int appId) => Dlcs.Where(item => item.AppId == appId).Select(item => item.DlcAppId).ToHashSet();

    /// <summary>逻辑日闭区间内的会话（按开始时间归属）。</summary>
    public IEnumerable<InsightSession> SessionsIn(string fromDay, string toDay)
        => Sessions.Where(session => string.CompareOrdinal(session.Day, fromDay) >= 0 && string.CompareOrdinal(session.Day, toDay) <= 0);

    public IEnumerable<InsightSession> SessionsIn(InsightPeriod period) => SessionsIn(period.FromDay, period.ToDay);
}

/// <summary>小工具：夹紧、均值标准差、循环均值。</summary>
public static class InsightMath
{
    public static double Clamp(double value, double minimum, double maximum) => Math.Min(maximum, Math.Max(minimum, value));

    public static double Hours(long seconds) => seconds / 3600.0;

    public static (double Mean, double Deviation) MeanDeviation(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return (0, 0);
        var mean = values.Average();
        var variance = values.Sum(value => (value - mean) * (value - mean)) / values.Count;
        return (mean, Math.Sqrt(variance));
    }

    /// <summary>
    /// 一组时刻的循环均值（小时，0~24）。23 点和 1 点的平均是 0 点，不是 12 点。
    /// 没有明确方向时（均匀分布）结果没有意义，调用方用 <paramref name="concentration"/> 判断。
    /// </summary>
    public static double CircularMeanHour(IEnumerable<double> hours, out double concentration)
    {
        double x = 0, y = 0;
        var count = 0;
        foreach (var hour in hours)
        {
            var angle = hour / 24 * 2 * Math.PI;
            x += Math.Cos(angle);
            y += Math.Sin(angle);
            count++;
        }
        if (count == 0)
        {
            concentration = 0;
            return 0;
        }
        concentration = Math.Sqrt(x * x + y * y) / count;
        var mean = Math.Atan2(y, x) / (2 * Math.PI) * 24;
        return mean < 0 ? mean + 24 : mean;
    }

    /// <summary>两个时刻的有符号差（小时，-12~12）。</summary>
    public static double HourDifference(double from, double to)
    {
        var difference = (to - from) % 24;
        if (difference > 12) difference -= 24;
        if (difference < -12) difference += 24;
        return difference;
    }
}
