using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Core.Features.Apps.Contracts;

public sealed record SteamAppArtwork(
    IReadOnlyList<string> Capsule,
    IReadOnlyList<string> Header,
    IReadOnlyList<string> Cover,
    IReadOnlyList<string> Hero);

public sealed record SteamAppMetadataSnapshot(
    uint AppId, string Name, string? Type, bool IsFree, SteamAppArtwork? Artwork = null);

public interface ISteamAppCatalogGateway
{
    Task<SteamGatewayResult<SteamAppMetadataSnapshot>> GetAppAsync(
        uint appId,
        string language,
        string? preferredAccountName = null,
        SteamRefreshMode refreshMode = SteamRefreshMode.PreferCache,
        CancellationToken cancellationToken = default);
}
