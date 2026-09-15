using System.Globalization;

namespace SteamStat.Core.Steam.Cache;

public static class SteamAchievementCacheKeys
{
    public const string SchemaResourceKind = "achievement-schema";
    public const string ProgressSummaryResourceKind = "achievement-progress-summary";
    public const string UnlocksResourceKind = "achievement-unlocks";
    public const string PublicScope = "public";
    public const int PayloadSchemaVersion = 1;
    public const string ProgressSummaryResourceId = "summary";

    public static SteamCacheKey Schema(uint appId, string language)
    {
        if (appId == 0) throw new ArgumentOutOfRangeException(nameof(appId));
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        return SteamCacheKey.Create(
            SchemaResourceKind,
            PublicScope,
            appId.ToString(CultureInfo.InvariantCulture),
            language,
            schemaVersion: PayloadSchemaVersion);
    }

    public static SteamCacheKey ProgressSummary(ulong steamId)
    {
        if (steamId == 0) throw new ArgumentOutOfRangeException(nameof(steamId));
        return SteamCacheKey.Create(
            ProgressSummaryResourceKind,
            steamId.ToString(CultureInfo.InvariantCulture),
            ProgressSummaryResourceId,
            schemaVersion: PayloadSchemaVersion);
    }

    public static SteamCacheKey Unlocks(ulong steamId, uint appId)
    {
        if (steamId == 0) throw new ArgumentOutOfRangeException(nameof(steamId));
        if (appId == 0) throw new ArgumentOutOfRangeException(nameof(appId));
        return SteamCacheKey.Create(
            UnlocksResourceKind,
            steamId.ToString(CultureInfo.InvariantCulture),
            appId.ToString(CultureInfo.InvariantCulture),
            schemaVersion: PayloadSchemaVersion);
    }
}
