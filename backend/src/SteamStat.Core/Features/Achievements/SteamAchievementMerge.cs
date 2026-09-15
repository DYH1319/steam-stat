using System.Globalization;
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

        var unlockEntries = unlocks.IsSuccess && unlocks.Value != null
            ? unlocks.Value.Entries
            : [];
        var byKey = new Dictionary<uint, List<int>>();
        var byName = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var index = 0; index < unlockEntries.Count; index++)
        {
            var unlock = unlockEntries[index];
            if (unlock.InternalKey is { } internalKey)
            {
                if (!byKey.TryGetValue(internalKey, out var keyList))
                    byKey[internalKey] = keyList = [];
                keyList.Add(index);
            }
            if (!string.IsNullOrEmpty(unlock.InternalName))
            {
                if (!byName.TryGetValue(unlock.InternalName, out var nameList))
                    byName[unlock.InternalName] = nameList = [];
                nameList.Add(index);
            }
        }

        var matched = new bool[unlockEntries.Count];
        var entries = new List<SteamAchievementEntry>(schema.Value.Definitions.Count);
        foreach (var definition in schema.Value.Definitions)
        {
            var candidateIndex = -1;
            var ambiguous = false;
            if (definition.InternalKey is { } definitionKey
                && byKey.TryGetValue(definitionKey, out var keyCandidates))
            {
                var candidateCount = 0;
                var firstCandidate = -1;
                foreach (var index in keyCandidates)
                {
                    if (matched[index]) continue;
                    candidateCount++;
                    if (firstCandidate < 0) firstCandidate = index;
                }
                if (candidateCount == 1) candidateIndex = firstCandidate;
                else if (candidateCount > 1) ambiguous = true;
            }
            if (!ambiguous && candidateIndex < 0
                && !string.IsNullOrEmpty(definition.InternalName)
                && byName.TryGetValue(definition.InternalName, out var nameCandidates))
            {
                var candidateCount = 0;
                var firstCandidate = -1;
                foreach (var index in nameCandidates)
                {
                    if (matched[index]) continue;
                    var unlock = unlockEntries[index];
                    if (definition.InternalKey is { } defKey
                        && unlock.InternalKey is { } unlockKey
                        && defKey != unlockKey)
                        continue;
                    candidateCount++;
                    if (firstCandidate < 0) firstCandidate = index;
                }
                if (candidateCount == 1) candidateIndex = firstCandidate;
            }
            if (candidateIndex >= 0)
            {
                var unlock = unlockEntries[candidateIndex];
                matched[candidateIndex] = true;
                entries.Add(new SteamAchievementEntry(
                    definition, unlock.IsUnlocked, unlock.UnlockTimeUtc));
            }
            else
            {
                entries.Add(new SteamAchievementEntry(definition, null, null));
            }
        }

        var unmatched = new List<string>();
        for (var index = 0; index < unlockEntries.Count; index++)
        {
            if (matched[index]) continue;
            var unlock = unlockEntries[index];
            unmatched.Add(
                !string.IsNullOrEmpty(unlock.InternalName)
                    ? unlock.InternalName
                    : string.Create(CultureInfo.InvariantCulture, $"internal-key:{unlock.InternalKey}"));
        }
        unmatched.Sort(StringComparer.Ordinal);

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
