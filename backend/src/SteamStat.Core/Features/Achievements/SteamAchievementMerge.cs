using System.Globalization;
using SteamStat.Core.Features.Achievements.Contracts;
using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Core.Features.Achievements;

public static class SteamAchievementMerge
{
    public static SteamAchievementGameResult Compose(
        uint appId,
        string language,
        SteamGatewayResult<SteamAchievementSchemaSnapshot> schema,
        SteamGatewayResult<SteamAchievementUnlockSnapshot> unlocks,
        SteamAppOwnership? ownership = null)
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

        var (kept, sets) = ApplySets(schema.Value, entries, ownership);
        var counted = sets.Where(set => set.Counted).ToArray();
        var total = counted.Sum(set => set.Total);
        var unlocked = counted.Sum(set => set.Unlocked);
        var summary = new SteamAchievementSummary(
            total,
            unlocked,
            kept.Count(entry => entry.IsCounted && entry.IsUnlocked == null),
            total == 0 ? null : unlocked * 100.0 / total,
            kept.Count,
            kept.Count(entry => entry.IsUnlocked == true));
        return new SteamAchievementGameResult(
            appId,
            language,
            schema.Value,
            kept,
            summary,
            schemaState,
            progressState,
            unmatched)
        {
            Sets = sets,
            OwnershipKnown = ownership is { IsComplete: true }
        };
    }

    /// <summary>
    /// 按成就集分组并判断哪些计入完成度。没有成就集的游戏也有一个 groupid=0 的基础组，
    /// schema 里没有分组时（公共数据旧记录）全部归到这个基础组，走同一条路。
    /// </summary>
    private static (List<SteamAchievementEntry> Entries, List<SteamAchievementSet> Sets) ApplySets(
        SteamAchievementSchemaSnapshot schema,
        IReadOnlyList<SteamAchievementEntry> entries,
        SteamAppOwnership? ownership)
    {
        var groups = schema.Groups.ToDictionary(group => group.GroupId);
        var classified = new SteamAchievementEntry?[entries.Count];
        var sets = new List<(uint Order, SteamAchievementSet Set)>();
        foreach (var indexed in entries.Select((entry, index) => (Entry: entry, Index: index)).GroupBy(item => item.Entry.Definition.GroupId ?? 0))
        {
            var members = indexed.Select(item => item.Entry).ToArray();
            var group = groups.GetValueOrDefault(indexed.Key);
            if (group is { DeveloperOnly: true } or { IsPublic: false }) continue;

            var dlcAppId = group?.DlcAppId;
            bool? owned = dlcAppId is not { } dlc || ownership == null
                ? null
                : ownership.AppIds.Contains(dlc) ? true : ownership.IsComplete ? false : null;
            var counted = dlcAppId == null || (owned ?? members.Any(entry => entry.IsUnlocked == true));
            var groupArchived = group?.Archived == true;
            var eligible = new List<SteamAchievementEntry>();
            foreach (var (entry, index) in indexed)
            {
                var archived = groupArchived || entry.Definition.Archived;
                var isEligible = !archived || entry.IsUnlocked == true;
                if (isEligible) eligible.Add(entry);
                classified[index] = entry with { IsArchived = archived, IsCounted = counted && isEligible };
            }
            // 未获得的归档集对这名玩家不存在
            if (eligible.Count == 0) continue;

            var unlocked = eligible.Where(entry => entry.IsUnlocked == true).ToArray();
            var completed = unlocked.Length == eligible.Count;
            sets.Add((group?.Order ?? 0, new SteamAchievementSet(
                indexed.Key,
                group?.LocalizedName ?? string.Empty,
                dlcAppId,
                groupArchived,
                counted,
                owned,
                eligible.Count,
                unlocked.Length,
                members.Length,
                completed,
                completed && unlocked.All(entry => entry.UnlockTimeUtc != null)
                    ? unlocked.Max(entry => entry.UnlockTimeUtc)
                    : null)));
        }
        // order 大多为 0，回退到 groupid
        return (
            classified.OfType<SteamAchievementEntry>().ToList(),
            sets.OrderBy(item => item.Order).ThenBy(item => item.Set.GroupId).Select(item => item.Set).ToList());
    }
}
