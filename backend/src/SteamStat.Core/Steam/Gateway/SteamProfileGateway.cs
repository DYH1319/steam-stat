using SteamStat.Core.Features.Profile.Contracts;
using SteamStat.Core.Steam.Gateway.Internal;

namespace SteamStat.Core.Steam.Gateway;

internal sealed class SteamProfileGateway(ISteamProfileSource source) : ISteamProfileGateway
{
    public Task<SteamGatewayResult<SteamProfileSnapshot>> GetProfileAsync(
        string accountName,
        ulong steamId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        if (steamId == 0)
            return Task.FromResult(SteamGatewayResult<SteamProfileSnapshot>.Failed(
                SteamFailureKind.NotFound, "invalid_steam_id"));
        return source.GetAsync(accountName, steamId, cancellationToken);
    }
}

internal sealed class SteamAvatarUriProvider : ISteamAvatarUriProvider
{
    private const string DefaultAvatarHash = "fef49e7fa7e1997310d705b2a6158ff8dc1cdfeb";
    private static readonly Uri BaseUri = new("https://avatars.akamai.steamstatic.com/");

    public Uri GetDefaultAvatarUri(SteamAvatarSize size) => CreateUri(DefaultAvatarHash, size);

    public Uri? GetAvatarUri(string? avatarHash, SteamAvatarSize size)
    {
        if (string.IsNullOrWhiteSpace(avatarHash)
            || avatarHash.Length != 40
            || avatarHash.Any(character => !Uri.IsHexDigit(character))) return null;
        return CreateUri(avatarHash.ToLowerInvariant(), size);
    }

    private static Uri CreateUri(string hash, SteamAvatarSize size)
        => new(BaseUri, $"{hash}{size switch
        {
            SteamAvatarSize.Full => "_full",
            SteamAvatarSize.Medium => "_medium",
            _ => string.Empty
        }}.jpg");
}
