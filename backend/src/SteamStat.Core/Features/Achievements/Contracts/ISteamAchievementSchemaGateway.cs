using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Core.Features.Achievements.Contracts;

public interface ISteamAchievementSchemaGateway
{
    Task<SteamGatewayResult<SteamAchievementSchemaSnapshot>> GetSchemaAsync(
        uint appId,
        string language,
        string? preferredAccountName = null,
        SteamRefreshMode refreshMode = SteamRefreshMode.PreferCache,
        CancellationToken cancellationToken = default);
}
