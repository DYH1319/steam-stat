using SteamStat.Core.Features.Friends;
using SteamStat.Core.Features.Library;
using SteamStat.Core.Steam.Cache;

namespace SteamStat.Core.Features;

public enum SteamFeatureResultStatus
{
    Success,
    Partial,
    Failure
}

public sealed record SteamLibraryResult(
    SteamFeatureResultStatus Status,
    IReadOnlyDictionary<string, IReadOnlyList<SteamOwnedGame>> Libraries,
    IReadOnlyList<SteamResourceStatus> Resources);

/// <summary>
/// 单账号游戏库的一页数据。分页下发是为了避免 Electron.NET socket.io 单条消息过大断连。
/// </summary>
public sealed record SteamLibraryPage(
    int Total,
    IReadOnlyList<SteamOwnedGame> Items,
    int? NextOffset);

public sealed record SteamFriendsResult(
    SteamFeatureResultStatus Status,
    IReadOnlyList<SteamFriendData> Accounts,
    IReadOnlyList<SteamResourceStatus> Resources);

internal static class SteamFeatureResultClassifier
{
    internal static SteamFeatureResultStatus Classify(
        IReadOnlyList<SteamResourceStatus> resources,
        bool hasData)
        => resources.Any(resource => resource.Failure.HasValue)
            ? hasData ? SteamFeatureResultStatus.Partial : SteamFeatureResultStatus.Failure
            : SteamFeatureResultStatus.Success;
}

public readonly record struct AppMetadata(uint AppId, string? Name);

public interface IAppNameResolver
{
    string? GetCachedName(uint appId);
    Task<string?> ResolveNameAsync(uint appId, CancellationToken cancellationToken = default);
}

public interface IAppMetadataWriter
{
    Task EnsureCachedAsync(IEnumerable<AppMetadata> apps, CancellationToken cancellationToken = default);
}

public interface ILanguageProvider
{
    string GetSteamLanguage();
}

public interface IRichPresenceResolver
{
    Task<string> ResolveAsync(
        string accountName,
        uint appId,
        IReadOnlyDictionary<string, string> richPresence,
        CancellationToken cancellationToken = default);
}

public sealed record FriendStatusValue(
    int? PersonaState = null,
    string? GameId = null,
    string? GameName = null,
    string? PersonaName = null,
    string? RichPresence = null,
    int? Relationship = null);

/// <summary>
/// 好友状态变化记录器。追踪名单跨会话持久化，登出不会清空。
/// </summary>
public interface IFriendStatusRecorder
{
    bool IsTracked(string accountName, string friendSteamId);

    Task RecordChangeAsync(
        string accountName,
        string friendSteamId,
        string friendPersonaName,
        string changeType,
        FriendStatusValue? previousValue,
        FriendStatusValue? currentValue,
        CancellationToken cancellationToken = default);
}
