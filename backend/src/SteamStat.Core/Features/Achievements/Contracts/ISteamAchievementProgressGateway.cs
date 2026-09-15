using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Core.Features.Achievements.Contracts;

public interface ISteamAchievementProgressGateway
{
    Task<SteamGatewayResult<IReadOnlyList<SteamAchievementAppProgressSnapshot>>> GetSummariesAsync(
        string accountName,
        IReadOnlyList<uint> appIds,
        SteamRefreshMode refreshMode = SteamRefreshMode.PreferCache,
        CancellationToken cancellationToken = default);

    Task<SteamGatewayResult<SteamAchievementUnlockSnapshot>> GetUnlocksAsync(
        string accountName,
        uint appId,
        SteamRefreshMode refreshMode = SteamRefreshMode.PreferCache,
        CancellationToken cancellationToken = default);
}
