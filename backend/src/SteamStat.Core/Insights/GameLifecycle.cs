namespace SteamStat.Core.Insights;

/// <summary>
/// 弃坑定义：没有手动状态时，超过 N 天未启动算暂停或弃坑，手动标记永远优先。
/// 这里不判定通关，只判定「不再玩了」。和语义视图 v_game_state、渲染层 effectiveStatus 同一套规则。
/// </summary>
public static class GameLifecycle
{
    public const string Unplayed = "unplayed";
    public const string Active = "active";
    public const string Paused = "paused";
    public const string Abandoned = "abandoned";

    public static string Infer(long? lastPlayed, long now, int pauseDays, int abandonDays)
    {
        if (lastPlayed is null or <= 0) return Unplayed;
        var idleDays = (now - lastPlayed.Value) / 86400;
        return idleDays >= abandonDays ? Abandoned : idleDays >= pauseDays ? Paused : Active;
    }

    /// <summary>手动状态里表示「还要玩」的两种。它们不会被推断成弃坑，也不进讣告。</summary>
    public static bool IsStillOn(string? manualStatus) => manualStatus is "playing" or "wantToPlay";
}
