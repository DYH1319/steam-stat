namespace ElectronNet.Models;

/// <summary>
/// Steam 官方累计时长的一段「平台期」：同一 (SteamId, AppId) 在 [FirstObservedAt, LastObservedAt] 内观测到的值不变。
/// 第一条即为该游戏的基线（安装本软件之前的历史）。
/// </summary>
public class PlaytimeSnapshot
{
    public int Id { get; init; }

    public string SteamId { get; init; } = string.Empty;

    public int AppId { get; init; }

    public int PlaytimeMinutes { get; init; }

    public string Source { get; set; } = "localconfig";

    public int FirstObservedAt { get; set; }

    public int LastObservedAt { get; set; }

    public int? LastPlayedAt { get; set; }
}
