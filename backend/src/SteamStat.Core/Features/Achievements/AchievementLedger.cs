namespace SteamStat.Core.Features.Achievements;

/// <summary>一个账号在档案里的一款游戏（achievement_game_archive）。AllTotal 为空表示成就集适配之前的旧口径。</summary>
public sealed record LedgerGameRow(
    string AccountName,
    uint AppId,
    string AppName,
    int Total,
    int Unlocked,
    int? AllTotal,
    int? AllUnlocked);

/// <summary>一个账号在档案里的一个成就集（achievement_set_archive）。Total 是这名玩家的口径：未获得的归档成就不计。</summary>
public sealed record LedgerSetRow(
    string AccountName,
    uint AppId,
    uint GroupId,
    string Name,
    uint? DlcAppId,
    bool Archived,
    bool Counted,
    bool? Owned,
    int Total,
    int Unlocked,
    int AllTotal,
    bool Completed,
    long? CompletedAt,
    long SyncedAt);

/// <summary>一个账号归档的一条解锁（achievement_unlock_archive）。</summary>
public sealed record LedgerUnlockRow(
    string AccountName,
    uint AppId,
    string InternalName,
    uint? GroupId,
    bool Archived,
    long UnlockTime);

public sealed record LedgerSet(
    uint GroupId,
    string Name,
    uint? DlcAppId,
    bool Archived,
    bool Counted,
    bool? Owned,
    int Total,
    int Unlocked,
    int AllTotal,
    bool Completed,
    long? CompletedAt,
    bool Synthetic);

public sealed record LedgerAccountProgress(string AccountName, int Total, int Unlocked, bool Completed);

/// <summary>
/// 多个账号合并后的一款游戏。Synthetic 表示合并后才完成：没有任何一个账号在 Steam 上完成了它。
/// </summary>
public sealed record LedgerGame(
    uint AppId,
    string AppName,
    int Total,
    int Unlocked,
    int? AllTotal,
    int? AllUnlocked,
    bool Completed,
    bool Synthetic,
    long? CompletedAt,
    IReadOnlyList<LedgerAccountProgress> Accounts,
    IReadOnlyList<LedgerSet> Sets);

/// <summary>
/// 多账号总账：成就取并集，时间取最早一次。每个账号的口径已由 <see cref="SteamAchievementMerge"/> 判断好，
/// 这里只在它们之上合并，不重新解释 schema：
/// <list type="bullet">
/// <item>集是否计入、DLC 是否拥有：任一账号算就算。</item>
/// <item>集的分母：未归档的成就数（各账号相同）加上任一账号获得过的归档成就。</item>
/// <item>完成状态：任一账号在 Steam 上完成过就保留（集完成后再加成就也不清除），否则看并集是否凑齐。</item>
/// </list>
/// 只有一个账号时结果与该账号自己的口径相同。
/// </summary>
public static class AchievementLedger
{
    private sealed record UnionEntry(uint GroupId, bool Archived, long FirstUnlock);

    public static IReadOnlyList<LedgerGame> Merge(
        IEnumerable<LedgerGameRow> games,
        IEnumerable<LedgerSetRow> sets,
        IEnumerable<LedgerUnlockRow> unlocks)
    {
        var setsByApp = sets.GroupBy(row => row.AppId).ToDictionary(group => group.Key, group => group.ToList());
        var unlocksByApp = unlocks.GroupBy(row => row.AppId).ToDictionary(group => group.Key, group => group.ToList());
        return games
            .GroupBy(row => row.AppId)
            .OrderBy(group => group.Key)
            .Select(group => MergeGame(
                group.Key,
                group.ToList(),
                setsByApp.GetValueOrDefault(group.Key) ?? [],
                unlocksByApp.GetValueOrDefault(group.Key) ?? []))
            .ToList();
    }

    private static LedgerGame MergeGame(
        uint appId,
        IReadOnlyList<LedgerGameRow> games,
        IReadOnlyList<LedgerSetRow> sets,
        IReadOnlyList<LedgerUnlockRow> unlocks)
    {
        var appName = games.Select(game => game.AppName).FirstOrDefault(name => !string.IsNullOrEmpty(name)) ?? string.Empty;
        var union = Union(unlocks);
        var accountSets = sets.GroupBy(set => set.AccountName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        // 没有任何账号按成就集算过：只能按旧口径合并
        if (sets.Count == 0 || games.All(game => game.AllTotal == null))
            return MergeLegacy(appId, appName, games, union);

        var merged = sets
            .GroupBy(set => set.GroupId)
            .Select(group => MergeSet(group.Key, group.ToList(), unlocks, union))
            .OrderBy(set => set.GroupId)
            .ToList();
        var counted = merged.Where(set => set.Counted).ToList();
        var total = counted.Sum(set => set.Total);
        var unlocked = counted.Sum(set => set.Unlocked);
        var allTotal = merged.Sum(set => set.AllTotal);
        var allUnlocked = Math.Min(allTotal, Math.Max(union.Count, games.Max(game => game.AllUnlocked ?? 0)));
        var completed = counted.Count > 0 && counted.All(set => set.Completed);

        var accounts = games
            .Select(game =>
            {
                var own = accountSets.GetValueOrDefault(game.AccountName) ?? [];
                var ownCounted = own.Where(set => set.Counted).ToList();
                var done = ownCounted.Count > 0
                    ? ownCounted.All(set => set.Completed)
                    : game.Total > 0 && game.Unlocked >= game.Total;
                return new LedgerAccountProgress(game.AccountName, game.Total, game.Unlocked, done);
            })
            .OrderBy(account => account.AccountName, StringComparer.Ordinal)
            .ToList();

        return new LedgerGame(
            appId,
            appName,
            total,
            unlocked,
            allTotal,
            allUnlocked,
            completed,
            completed && !accounts.Any(account => account.Completed),
            completed && counted.All(set => set.CompletedAt != null) ? counted.Max(set => set.CompletedAt) : null,
            accounts,
            merged);
    }

    private static LedgerSet MergeSet(
        uint groupId,
        IReadOnlyList<LedgerSetRow> rows,
        IReadOnlyList<LedgerUnlockRow> unlocks,
        IReadOnlyDictionary<string, UnionEntry> union)
    {
        var latest = rows.MaxBy(row => row.SyncedAt)!;
        // 每个账号的分母 = 未归档的成就 + 它自己获得的归档成就，减掉后者就是各账号共同的部分
        var archivedByAccount = unlocks
            .Where(unlock => unlock.Archived && GroupOf(unlock, union) == groupId)
            .GroupBy(unlock => unlock.AccountName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(unlock => unlock.InternalName).Distinct(StringComparer.Ordinal).Count(), StringComparer.Ordinal);
        var shared = rows.Max(row => Math.Max(0, row.Total - archivedByAccount.GetValueOrDefault(row.AccountName)));
        var members = union.Values.Where(entry => entry.GroupId == groupId).ToList();
        var allTotal = rows.Max(row => row.AllTotal);
        var total = Math.Max(shared + members.Count(entry => entry.Archived), rows.Max(row => row.Total));
        if (allTotal > 0) total = Math.Min(total, allTotal);
        var unlocked = Math.Min(total, Math.Max(members.Count, rows.Max(row => row.Unlocked)));

        var completedRows = rows.Where(row => row.Completed).ToList();
        var completed = completedRows.Count > 0 || (total > 0 && unlocked >= total);
        var synthetic = completed && completedRows.Count == 0;
        long? completedAt = completedRows.Count > 0
            ? completedRows.Min(row => row.CompletedAt)
            // 并集凑齐的时刻：每个成就取最早一次解锁，其中最晚的那个
            : synthetic && members.Count >= total ? members.Max(entry => entry.FirstUnlock) : null;

        return new LedgerSet(
            groupId,
            latest.Name,
            latest.DlcAppId,
            latest.Archived,
            rows.Any(row => row.Counted),
            rows.Any(row => row.Owned == true) ? true : rows.Any(row => row.Owned == null) ? null : false,
            total,
            unlocked,
            allTotal,
            completed,
            completedAt,
            synthetic);
    }

    private static LedgerGame MergeLegacy(
        uint appId,
        string appName,
        IReadOnlyList<LedgerGameRow> games,
        IReadOnlyDictionary<string, UnionEntry> union)
    {
        var total = games.Max(game => game.Total);
        var unlocked = Math.Min(total, Math.Max(union.Count, games.Max(game => game.Unlocked)));
        var accounts = games
            .Select(game => new LedgerAccountProgress(game.AccountName, game.Total, game.Unlocked, game.Total > 0 && game.Unlocked >= game.Total))
            .OrderBy(account => account.AccountName, StringComparer.Ordinal)
            .ToList();
        var completed = total > 0 && unlocked >= total;
        var synthetic = completed && !accounts.Any(account => account.Completed);
        return new LedgerGame(
            appId,
            appName,
            total,
            unlocked,
            null,
            null,
            completed,
            synthetic,
            synthetic && union.Count >= total ? union.Values.Max(entry => entry.FirstUnlock) : null,
            accounts,
            []);
    }

    /// <summary>同一个成就按内部名合并：最早的解锁时间，归档标记任一行有就算。</summary>
    private static Dictionary<string, UnionEntry> Union(IEnumerable<LedgerUnlockRow> unlocks)
    {
        var union = new Dictionary<string, UnionEntry>(StringComparer.Ordinal);
        foreach (var unlock in unlocks)
        {
            if (union.TryGetValue(unlock.InternalName, out var known))
                union[unlock.InternalName] = new UnionEntry(
                    // 旧记录没有分组：用别的账号记下的分组
                    unlock.GroupId ?? known.GroupId,
                    known.Archived || unlock.Archived,
                    unlock.UnlockTime > 0 && (known.FirstUnlock <= 0 || unlock.UnlockTime < known.FirstUnlock) ? unlock.UnlockTime : known.FirstUnlock);
            else
                union[unlock.InternalName] = new UnionEntry(unlock.GroupId ?? 0, unlock.Archived, unlock.UnlockTime);
        }
        return union;
    }

    private static uint GroupOf(LedgerUnlockRow unlock, IReadOnlyDictionary<string, UnionEntry> union)
        => union.TryGetValue(unlock.InternalName, out var entry) ? entry.GroupId : unlock.GroupId ?? 0;
}
