using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Core.Features.Profile.Contracts;

public sealed record SteamProfileSnapshot(
    ulong SteamId,
    string? PersonaName,
    int? Level,
    string? AvatarHash);

public interface ISteamProfileGateway
{
    Task<SteamGatewayResult<SteamProfileSnapshot>> GetProfileAsync(
        string accountName,
        ulong steamId,
        CancellationToken cancellationToken = default);
}

public enum SteamAvatarSize
{
    Small,
    Medium,
    Full
}

public interface ISteamAvatarUriProvider
{
    Uri GetDefaultAvatarUri(SteamAvatarSize size);
    Uri? GetAvatarUri(string? avatarHash, SteamAvatarSize size);
}
