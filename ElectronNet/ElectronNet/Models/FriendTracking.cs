namespace ElectronNet.Models;

/// <summary>
/// 手动选择追踪的好友（按登录账号区分，跨会话持久化）
/// </summary>
public class FriendTracking
{
    public int Id { get; init; }

    public string AccountName { get; init; } = string.Empty;

    public string FriendSteamId { get; init; } = string.Empty;

    public long CreatedAt { get; init; }
}
