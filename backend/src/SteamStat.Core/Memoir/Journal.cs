namespace SteamStat.Core.Memoir;

/// <summary>
/// 时光胶囊：写给未来的一段话，绑定日期或游戏，到期才能打开。
/// 绑定游戏的胶囊要过了日期、再启动这款游戏时才开，比如「下次回到这款游戏时读」。
/// </summary>
public static class TimeCapsuleRules
{
    public const int MaxMessageLength = 4000;
    public const int MaxYears = 50;

    /// <summary>到了日期；绑定游戏的还要在那之后启动过这款游戏。</summary>
    public static bool IsReady(long openAt, int? appId, long now, long? lastLaunch)
        => now >= openAt && (appId == null || lastLaunch is { } launch && launch >= openAt);

    /// <summary>不合法时返回原因：empty / tooLong / tooSoon / tooLate。</summary>
    public static string? Validate(string message, long openAt, long now)
    {
        if (string.IsNullOrWhiteSpace(message)) return "empty";
        if (message.Length > MaxMessageLength) return "tooLong";
        // 至少明天：今天写今天开不叫胶囊
        if (openAt < now + 12 * 3600) return "tooSoon";
        if (openAt > now + MaxYears * 366L * 86400) return "tooLate";
        return null;
    }
}

/// <summary>回坑「上回说到」：时隔很久再启动一款游戏时，把上次玩到哪里找出来。</summary>
public static class PreviouslyOnRules
{
    /// <summary>「上次那段时间」：最后一次游玩之前的 30 天。</summary>
    public const long PeriodSeconds = 30 * 86400L;

    /// <summary>距上次游玩的天数；不够 <paramref name="thresholdDays"/> 时返回 null。</summary>
    public static int? GapDays(long previousEnd, long start, int thresholdDays)
    {
        if (previousEnd <= 0 || start <= previousEnd) return null;
        var days = (int)((start - previousEnd) / 86400);
        return days >= thresholdDays ? days : null;
    }
}
