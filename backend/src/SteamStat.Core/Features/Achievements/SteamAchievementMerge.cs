using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Core.Features.Achievements;

public static class SteamAchievementMerge
{
    public static SteamAchievementGameResult Compose(
        uint appId,
        string language,
        SteamGatewayResult<SteamAchievementSchemaSnapshot> schema,
        SteamGatewayResult<SteamAchievementUnlockSnapshot> unlocks)
    {
        var schemaState = SteamAchievementResourceState.From(schema);
        var progressState = SteamAchievementResourceState.From(unlocks);
        if (!schema.IsSuccess || schema.Value == null)
            return new SteamAchievementGameResult(
                appId,
                language,
                null,
                [],
                new SteamAchievementSummary(0, 0, 0, null),
                schemaState,
                progressState,
                []);

        var unlocksByName = new Dictionary<string, SteamAchievementUnlock>(StringComparer.Ordinal);
        if (unlocks.IsSuccess && unlocks.Value != null)
            foreach (var unlock in unlocks.Value.Entries)
                unlocksByName.TryAdd(unlock.InternalName, unlock);

        var matched = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<SteamAchievementEntry>(schema.Value.Definitions.Count);
        foreach (var definition in schema.Value.Definitions)
        {
            if (unlocksByName.TryGetValue(definition.InternalName, out var unlock)
                && !(definition.InternalKey is { } definitionKey
                    && unlock.InternalKey is { } unlockKey
                    && definitionKey != unlockKey))
            {
                matched.Add(definition.InternalName);
                entries.Add(new SteamAchievementEntry(definition, unlock.IsUnlocked, unlock.UnlockTimeUtc));
            }
            else
            {
                entries.Add(new SteamAchievementEntry(definition, null, null));
            }
        }
        var unmatched = unlocksByName.Keys
            .Where(name => !matched.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var total = entries.Count;
        var unlocked = entries.Count(entry => entry.IsUnlocked == true);
        var unknown = entries.Count(entry => entry.IsUnlocked == null);
        var summary = new SteamAchievementSummary(
            total,
            unlocked,
            unknown,
            total == 0 ? null : unlocked * 100.0 / total);
        return new SteamAchievementGameResult(
            appId,
            language,
            schema.Value,
            entries,
            summary,
            schemaState,
            progressState,
            unmatched);
    }
}
