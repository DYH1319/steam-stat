using SteamStat.Core.Features.Achievements;
using SteamStat.Core.Features.Friends.Contracts;
using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Core.Features.Friends;

/// <summary>
/// 好友对比：按需读取好友公开的游戏库与成就解锁情况，不缓存、不落库。
/// </summary>
public sealed class SteamFriendCompareService(ISteamFriendProfileGateway gateway)
{
    private const string InvalidSteamId = "friend_profile_invalid_steam_id";

    public Task<SteamGatewayResult<SteamFriendLibrarySnapshot>> GetLibraryAsync(
        string accountName,
        string friendSteamId,
        CancellationToken cancellationToken = default)
        => ulong.TryParse(friendSteamId, out var steamId) && steamId != 0
            ? gateway.GetLibraryAsync(accountName, steamId, cancellationToken)
            : Task.FromResult(SteamGatewayResult<SteamFriendLibrarySnapshot>.Failed(
                SteamFailureKind.InvalidData, InvalidSteamId));

    public Task<SteamGatewayResult<SteamAchievementUnlockSnapshot>> GetAchievementUnlocksAsync(
        string accountName,
        string friendSteamId,
        uint appId,
        CancellationToken cancellationToken = default)
        => ulong.TryParse(friendSteamId, out var steamId) && steamId != 0
            ? gateway.GetAchievementUnlocksAsync(accountName, steamId, appId, cancellationToken)
            : Task.FromResult(SteamGatewayResult<SteamAchievementUnlockSnapshot>.Failed(
                SteamFailureKind.InvalidData, InvalidSteamId));
}
