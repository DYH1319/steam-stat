using SteamStat.Core.Sessions;

namespace SteamStat.Core.Steam.Session;

internal sealed class SteamAccountIdentityProvider(ISteamSessionAccessor sessionAccessor) : ISteamAccountIdentityProvider
{
    public bool TryGetSteamId(string accountName, out ulong steamId)
    {
        steamId = 0;
        if (string.IsNullOrWhiteSpace(accountName) || !sessionAccessor.TryGetSession(accountName, out var session))
            return false;
        steamId = session.Client.SteamID?.ConvertToUInt64() ?? 0;
        return steamId != 0;
    }
}
