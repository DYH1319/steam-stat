using System.Globalization;
using SteamKit2;
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
            if (item.appid == 0)
                throw new InvalidDataException("Achievement progress item has a zero app id.");
            if (!appIds.Add(item.appid))
                throw new InvalidDataException($"Duplicate achievement progress app id '{item.appid}'.");
            if (item.total > int.MaxValue)
                throw new InvalidDataException(
                    $"Achievement progress total for app '{item.appid}' exceeds the supported range.");
            if (item.unlocked > item.total)
                throw new InvalidDataException(
                    $"Achievement progress for app '{item.appid}' has more unlocked than total.");
            if (float.IsNaN(item.percentage)
                || float.IsInfinity(item.percentage)
                || item.percentage < 0
                || item.percentage > 100)
                throw new InvalidDataException(
                    $"Achievement progress percentage for app '{item.appid}' is out of range.");
            snapshots.Add(new SteamAchievementAppProgressSnapshot(
                item.appid, (int)item.total, (int)item.unlocked, (double)item.percentage));
        }
        return snapshots;
    }

    public static AchievementUserStatsMapResult MapUserStats(CMsgClientGetUserStatsResponse response)
    {
        if (response == null)
            throw new InvalidDataException("Achievement user stats response must not be null.");
        var schemaBytes = response.schema;
        if (schemaBytes == null || schemaBytes.Length == 0)
        {
            if (response.achievement_blocks == null || response.achievement_blocks.Count == 0)
                return new AchievementUserStatsMapResult([], []);
            throw new InvalidDataException(
                "Achievement user stats response has achievement blocks but an empty schema.");
        }
        var root = new KeyValue();
        bool parsed;
        using (var stream = new MemoryStream(schemaBytes, writable: false))
            parsed = root.TryReadAsBinary(stream);
        if (!parsed)
            throw new InvalidDataException(
                "Achievement user stats schema is not valid binary KeyValues.");
        var stats = string.Equals(root.Name, "stats", StringComparison.OrdinalIgnoreCase)
            ? root
            : FindChild(root, "stats");
        if (stats == null)
            foreach (var wrapper in root.Children)
            {
                stats = FindChild(wrapper, "stats");
                if (stats != null) break;
            }
        if (stats == null)
            throw new InvalidDataException("Achievement user stats schema has no stats section.");

        var coordinates = new HashSet<AchievementProtocolCoordinate>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var definitions = new List<(uint StatId, uint Bit, string InternalName)>();
        foreach (var statNode in stats.Children)
        {
            if (!uint.TryParse(
                    statNode.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var statId))
                continue;
            var bits = FindChild(statNode, "bits");
            if (bits == null) continue;
            foreach (var bitNode in bits.Children)
            {
                if (!uint.TryParse(
                        bitNode.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bit))
                    continue;
                var internalName = FindChild(bitNode, "name")?.Value;
                if (string.IsNullOrWhiteSpace(internalName))
                    throw new InvalidDataException("Achievement definition has an empty internal name.");
                if (!coordinates.Add(new AchievementProtocolCoordinate(statId, bit)))
                    throw new InvalidDataException(
                        $"Duplicate achievement coordinate ({statId},{bit}).");
                if (!names.Add(internalName))
                    throw new InvalidDataException(
                        $"Duplicate achievement internal name '{internalName}'.");
                definitions.Add((statId, bit, internalName));
            }
        }

        var blocks = new Dictionary<uint, IReadOnlyList<uint>>();
        foreach (var block in response.achievement_blocks ?? [])
        {
            if (block.achievement_id == 0)
                throw new InvalidDataException("Achievement block has a zero achievement id.");
            if (!blocks.TryAdd(block.achievement_id, block.unlock_time ?? []))
                throw new InvalidDataException($"Duplicate achievement block '{block.achievement_id}'.");
        }

        var entries = new List<SteamAchievementUnlock>(definitions.Count);
        foreach (var definition in definitions)
        {
            bool? isUnlocked = null;
            DateTimeOffset? unlockTimeUtc = null;
            if (blocks.TryGetValue(definition.StatId, out var unlockTimes)
                && definition.Bit < unlockTimes.Count)
            {
                var seconds = unlockTimes[(int)definition.Bit];
                if (seconds == 0)
                {
                    isUnlocked = false;
                }
                else
                {
                    isUnlocked = true;
                    unlockTimeUtc = ToUnlockTimeUtc(seconds);
                }
            }
            entries.Add(new SteamAchievementUnlock(
                null, definition.InternalName, isUnlocked, unlockTimeUtc));
        }

        var unmatched = new List<AchievementProtocolCoordinate>();
        foreach (var (achievementId, unlockTimes) in blocks)
        {
            for (var bit = 0; bit < unlockTimes.Count; bit++)
            {
                if (unlockTimes[bit] == 0) continue;
                var coordinate = new AchievementProtocolCoordinate(achievementId, (uint)bit);
                if (!coordinates.Contains(coordinate)) unmatched.Add(coordinate);
            }
        }
        unmatched.Sort((left, right) =>
            left.StatId != right.StatId
                ? left.StatId.CompareTo(right.StatId)
                : left.Bit.CompareTo(right.Bit));

        return new AchievementUserStatsMapResult(entries, unmatched);
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

    private static KeyValue? FindChild(KeyValue node, string name)
        => node.Children.FirstOrDefault(
            child => string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase));

    private static DateTimeOffset ToUnlockTimeUtc(uint seconds)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException($"Unlock timestamp '{seconds}' is out of range.", exception);
        }
    }

    private static double? ParsePercent(string? value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return null;
        if (double.IsNaN(parsed) || double.IsInfinity(parsed) || parsed < 0 || parsed > 100)
            return null;
        return parsed;
    }
}
