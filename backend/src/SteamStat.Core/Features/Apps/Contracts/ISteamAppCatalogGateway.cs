using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Core.Features.Apps.Contracts;

public sealed record SteamAppMetadataSnapshot(uint AppId, string Name, string? Type, bool IsFree);

public interface ISteamAppCatalogGateway
{
    Task<SteamGatewayResult<SteamAppMetadataSnapshot>> GetAppAsync(
        uint appId,
        string language,
        string? preferredAccountName = null,
        SteamRefreshMode refreshMode = SteamRefreshMode.PreferCache,
        CancellationToken cancellationToken = default);
}
