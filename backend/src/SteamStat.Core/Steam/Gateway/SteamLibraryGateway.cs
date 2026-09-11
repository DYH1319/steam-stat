using SteamStat.Core.Features.Library.Contracts;
using SteamStat.Core.Steam.Gateway.Internal;

namespace SteamStat.Core.Steam.Gateway;

internal sealed class SteamLibraryGateway(ISteamLibrarySource source) : ISteamLibraryGateway
{
    public Task<SteamGatewayResult<SteamLibrarySnapshot>> GetLibraryAsync(
        string accountName,
        bool includeFamilyShared,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        return source.GetAsync(accountName, includeFamilyShared, cancellationToken);
    }
}
