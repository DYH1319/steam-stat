using System.Globalization;
using SteamKit2.Internal;
using SteamStat.Core.Features.Achievements;

namespace SteamStat.Core.Steam.Gateway.Internal;

internal static class AchievementProtocolMapper
{
    public static SteamAchievementSchemaSnapshot MapSchema(
        uint appId,
        string language,
        AchievementSchemaResponse response)
    {
        if (response == null)
            throw new InvalidDataException("Achievement schema response must not be null.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var internalKeys = new HashSet<uint>();
        var definitions = new List<SteamAchievementDefinition>(response.achievements.Count);
        foreach (var achievement in response.achievements)
        {
            if (string.IsNullOrWhiteSpace(achievement.internal_name))
                throw new InvalidDataException("Achievement definition has an empty internal name.");
            if (!names.Add(achievement.internal_name))
                throw new InvalidDataException(
                    $"Duplicate achievement internal name '{achievement.internal_name}'.");
            if (achievement.internal_key is { } internalKey && !internalKeys.Add(internalKey))
                throw new InvalidDataException($"Duplicate achievement internal key '{internalKey}'.");
            var progressType = MapProgressType(achievement.progress_type);
            double? minProgress = progressType switch
            {
                SteamAchievementProgressType.Int => achievement.min_progress_int,
                SteamAchievementProgressType.Float => achievement.min_progress_float,
                _ => null
            };
            double? maxProgress = progressType switch
            {
                SteamAchievementProgressType.Int => achievement.max_progress_int,
                SteamAchievementProgressType.Float => achievement.max_progress_float,
                _ => null
            };
            definitions.Add(new SteamAchievementDefinition(
                achievement.internal_key,
                achievement.internal_name,
                achievement.localized_name ?? string.Empty,
                achievement.localized_desc ?? string.Empty,
                achievement.icon ?? string.Empty,
                achievement.icon_gray ?? string.Empty,
                achievement.hidden,
                ParsePercent(achievement.player_percent_unlocked),
                achievement.groupid,
                achievement.archived,
                progressType,
                minProgress,
                maxProgress));
        }
        var groupIds = new HashSet<uint>();
        var groups = new List<SteamAchievementGroup>(response.groups.Count);
        foreach (var group in response.groups)
        {
            if (!groupIds.Add(group.groupid))
                throw new InvalidDataException($"Duplicate achievement group '{group.groupid}'.");
            groups.Add(new SteamAchievementGroup(
                group.groupid,
                group.localized_name ?? string.Empty,
                group.dlcappid is null or 0 ? null : group.dlcappid,
                group.archived,
                group.developeronly,
                group.ispublic,
                group.order));
        }
        return new SteamAchievementSchemaSnapshot(
            appId,
            language,
            response.schema_version ?? 0,
            response.schema_hash ?? 0,
            definitions,
            groups);
    }

    public static IReadOnlyList<SteamAchievementAppProgressSnapshot> MapProgressSummaries(
        IEnumerable<CPlayer_GetAchievementsProgress_Response.AchievementProgress> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var appIds = new HashSet<uint>();
        var snapshots = new List<SteamAchievementAppProgressSnapshot>();
        foreach (var item in items)
        {
            if (!appIds.Add(item.appid))
                throw new InvalidDataException($"Duplicate achievement progress app id '{item.appid}'.");
            if (item.unlocked > item.total)
                throw new InvalidDataException(
                    $"Achievement progress for app '{item.appid}' has more unlocked than total.");
            snapshots.Add(new SteamAchievementAppProgressSnapshot(
                item.appid, (int)item.total, (int)item.unlocked, (double)item.percentage));
        }
        return snapshots;
    }

    public static SteamAchievementUnlockSnapshot MapUnlocks(
        ulong steamId,
        uint appId,
        long sessionGeneration,
        IReadOnlyList<DecodedAchievementUnlock> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return new SteamAchievementUnlockSnapshot(
            steamId,
            appId,
            sessionGeneration,
            entries.Select(entry => new SteamAchievementUnlock(
                    entry.InternalKey, entry.InternalName, entry.IsUnlocked, entry.UnlockTimeUtc))
                .ToArray());
    }

    public static SteamAchievementProgressType MapProgressType(int progressType) => progressType switch
    {
        0 => SteamAchievementProgressType.None,
        1 => SteamAchievementProgressType.Int,
        2 => SteamAchievementProgressType.Float,
        _ => SteamAchievementProgressType.Unknown
    };

    private static double? ParsePercent(string? value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return null;
        if (double.IsNaN(parsed) || double.IsInfinity(parsed) || parsed < 0 || parsed > 100)
            return null;
        return parsed;
    }
}
