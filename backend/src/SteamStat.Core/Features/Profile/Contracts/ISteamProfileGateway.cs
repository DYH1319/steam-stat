using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Core.Features.Profile.Contracts;

public sealed record SteamProfileSnapshot(
    ulong SteamId,
    string? PersonaName,
    int? Level,
    string? AvatarHash);

/// <summary>
/// 公开资料里的装饰信息，任何账号都能取到，不需要登录。
/// </summary>
public sealed record SteamPublicProfileSnapshot(
    int? Level,
    string? LevelClass,
    string? AvatarFrameUrl,
    string? AnimatedAvatarUrl,
    /// <summary>公开昵称：给拿不到 CM 资料的人（家庭共享成员等）补名字用</summary>
    string? PersonaName = null);

/// <summary>
/// 资料页上展示的徽章（Steam 只经 CM 提供「展示徽章」，完整徽章列表需要 Web API 访问令牌）。
/// </summary>
public sealed record SteamFavoriteBadgeSnapshot(
    bool HasBadge,
    uint BadgeId,
    uint Level,
    uint AppId,
    ulong CommunityItemId,
    uint BorderColor);

/// <summary>
/// 游戏 / 活动徽章的显示名：BadgeName 来自积分商店的徽章定义（活动徽章），AppName 来自社区应用信息（活动与游戏均有）。
/// </summary>
public sealed record SteamBadgeNameSnapshot(
    uint AppId,
    string? BadgeName,
    string? AppName);

public interface ISteamProfileGateway
{
    Task<SteamGatewayResult<SteamProfileSnapshot>> GetProfileAsync(
        string accountName,
        ulong steamId,
        CancellationToken cancellationToken = default);

    Task<SteamGatewayResult<SteamPublicProfileSnapshot>> GetPublicProfileAsync(
        ulong steamId,
        CancellationToken cancellationToken = default);

    Task<SteamGatewayResult<SteamFavoriteBadgeSnapshot>> GetFavoriteBadgeAsync(
        string accountName,
        ulong steamId,
        CancellationToken cancellationToken = default);

    /// <summary>language 为 Steam 语言代码（english / schinese / tchinese …）。</summary>
    Task<SteamGatewayResult<SteamBadgeNameSnapshot>> GetBadgeNameAsync(
        string accountName,
        uint appId,
        string language,
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
