using SteamStat.Core.Features.Achievements;
using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Core.Features.Friends.Contracts;

public sealed record SteamFriendOwnedGame(
    int AppId,
    string Name,
    int PlaytimeForever,
    int Playtime2Weeks,
    int RtimeLastPlayed,
    bool HasCommunityVisibleStats,
    int AchievementTotal,
    int AchievementUnlocked,
    double AchievementPercentage);

public sealed record SteamFriendLibrarySnapshot(
    ulong SteamId,
    IReadOnlyList<SteamFriendOwnedGame> Games);

/// <summary>
/// 通过 CM 读取好友公开的游戏库与成就（受好友隐私设置限制，只读）。
/// </summary>
public interface ISteamFriendProfileGateway
{
    Task<SteamGatewayResult<SteamFriendLibrarySnapshot>> GetLibraryAsync(
        string accountName,
        ulong friendSteamId,
        CancellationToken cancellationToken = default);

    Task<SteamGatewayResult<SteamAchievementUnlockSnapshot>> GetAchievementUnlocksAsync(
        string accountName,
        ulong friendSteamId,
        uint appId,
        CancellationToken cancellationToken = default);
}
