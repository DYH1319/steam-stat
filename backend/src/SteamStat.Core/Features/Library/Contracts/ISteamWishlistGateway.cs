using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Core.Features.Library.Contracts;

public interface ISteamWishlistGateway
{
    Task<SteamGatewayResult<IReadOnlyList<uint>>> GetWishlistAsync(
        ulong steamId,
        SteamRefreshMode refreshMode = SteamRefreshMode.PreferCache,
        CancellationToken cancellationToken = default);
}
