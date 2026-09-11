using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Core.Features.Friends.Contracts;

public interface ISteamPresenceLocalizationGateway
{
    Task<SteamGatewayResult<IReadOnlyDictionary<string, string>>> GetLocalizationAsync(
        string accountName,
        uint appId,
        string language,
        SteamRefreshMode refreshMode = SteamRefreshMode.PreferCache,
        CancellationToken cancellationToken = default);
}
