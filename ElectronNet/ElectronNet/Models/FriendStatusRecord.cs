namespace ElectronNet.Models;

public class FriendStatusRecord
{
    public int Id { get; init; }

    /// <summary>
    /// 登录用户账户名（哪个账号记录下的变化）
    /// </summary>
    public string AccountName { get; set; } = string.Empty;

    /// <summary>
    /// 被记录好友的 Steam ID
    /// </summary>
    public string FriendSteamId { get; set; } = string.Empty;

    /// <summary>
    /// 好友昵称（变化发生时的昵称快照）
    /// </summary>
    public string FriendPersonaName { get; set; } = string.Empty;

    /// <summary>
    /// 变化类型（state / game / personaName）
    /// </summary>
    public string ChangeType { get; set; } = string.Empty;

    /// <summary>
    /// 变化前的值（JSON 字符串，根据 ChangeType 存储不同内容）
    /// </summary>
    public string? PreviousValue { get; set; }

    /// <summary>
    /// 变化后的值（JSON 字符串）
    /// </summary>
    public string? CurrentValue { get; set; }

    /// <summary>
    /// 变化发生的 Unix 时间戳（秒）
    /// </summary>
    public long Timestamp { get; set; }
}
