using SteamKit2;

namespace SteamStat.Core.Features;

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
        SteamClient client,
        uint appId,
        IReadOnlyDictionary<string, string> richPresence,
        CancellationToken cancellationToken = default);
}

public interface IFriendStatusRecorder
{
    bool IsTracked(string accountName, string friendSteamId);

    Task RecordChangeAsync(
        string accountName,
        string friendSteamId,
        string friendPersonaName,
        string changeType,
        object? previousValue,
        object? currentValue,
        CancellationToken cancellationToken = default);

    void ClearTrackingForAccount(string accountName);
}
