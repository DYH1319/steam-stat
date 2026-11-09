using System.Globalization;

namespace SteamStat.Core.Insights;

/// <summary>看哪些账号。空表示全部本机账号。</summary>
public sealed record InsightScope(IReadOnlySet<string>? SteamIds = null)
{
    public static InsightScope All { get; } = new();

    public static InsightScope Of(IEnumerable<string>? steamIds)
    {
        var set = steamIds?.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
        return set is { Count: > 0 } ? new InsightScope(set) : All;
    }

    public bool Includes(string? steamId) => SteamIds == null || (steamId != null && SteamIds.Contains(steamId));
}

/// <summary>语义视图之外的数据：游戏库快照、公开资料和对账结果。由 Host 提供。</summary>
public interface IInsightCatalog
{
    Task<IReadOnlyList<InsightOwnedGame>> GetOwnedGamesAsync(InsightScope scope, CancellationToken cancellationToken = default);

    IReadOnlyDictionary<int, InsightAppFacts> GetAppFacts(IEnumerable<int> appIds);

    Task<InsightReconciliation?> GetReconciliationAsync(CancellationToken cancellationToken = default);
}

/// <summary>把语义视图读成 <see cref="InsightArchive"/>。这是洞察里唯一写 SQL 的地方。</summary>
public static class InsightArchiveLoader
{
    private static readonly IReadOnlyDictionary<string, object?> NoParameters = new Dictionary<string, object?>();

    /// <param name="friendsFrom">
    /// 好友记录从什么时候开始读。十年档案有上百万条，周刊只要最近九周，要全部读完的只有生涯高光。
    /// </param>
    public static async Task<InsightArchive> LoadAsync(
        IInsightDataSource data,
        IInsightCatalog catalog,
        InsightOptions options,
        InsightScope scope,
        long now,
        long friendsFrom,
        CancellationToken cancellationToken = default)
    {
        async Task<IReadOnlyList<object?[]>> Query(string sql, IReadOnlyDictionary<string, object?>? parameters = null)
            => await data.QueryAsync(sql, parameters ?? NoParameters, cancellationToken).ConfigureAwait(false);

        var sessions = (await Query("""
                SELECT session_id, steam_id, app_id, app_name, start_time, end_time, duration_seconds, idle_seconds, logical_day, auto_tags, build_id
                FROM v_session ORDER BY start_time, session_id
                """).ConfigureAwait(false))
            .Where(row => scope.Includes(Text(row[1])))
            .Select(row => new InsightSession(
                Long(row[0]),
                Text(row[1]) ?? string.Empty,
                Int(row[2]),
                Text(row[3]) ?? $"App {Int(row[2])}",
                Long(row[4]),
                Long(row[5]),
                Long(row[6]),
                Long(row[7]),
                Text(row[8]) ?? string.Empty,
                (SessionTags)Int(row[9]),
                NullableInt(row[10])))
            .ToList();

        var unlocks = (await Query("SELECT steam_id, app_id, internal_name, name, global_percent, unlock_time, logical_day FROM v_achievement_unlock ORDER BY unlock_time").ConfigureAwait(false))
            .Where(row => scope.Includes(Text(row[0])))
            .Select(row => new InsightUnlock(Int(row[1]), Text(row[2]) ?? string.Empty, Text(row[3]) ?? string.Empty, NullableDouble(row[4]), Long(row[5]), Text(row[6]) ?? string.Empty))
            .ToList();

        var progress = (await Query("SELECT steam_id, app_id, app_name, total, unlocked FROM v_achievement_game").ConfigureAwait(false))
            .Where(row => scope.Includes(Text(row[0])))
            .Select(row => new InsightProgress(Int(row[1]), Text(row[2]) ?? $"App {Int(row[1])}", Int(row[3]), Int(row[4])))
            .ToList();

        var setRows = (await Query("SELECT steam_id, app_id, group_id, name, dlc_app_id, completed, completed_at FROM v_achievement_set").ConfigureAwait(false))
            .Where(row => scope.Includes(Text(row[0])))
            .ToList();
        var setCompletions = setRows
            .Where(row => Int(row[5]) != 0 && Long(row[6]) > 0)
            .Select(row => new InsightSetCompletion(Int(row[1]), Long(row[2]), Text(row[3]) ?? string.Empty, Long(row[6])))
            .ToList();
        var dlcs = setRows
            .Where(row => Long(row[4]) > 0)
            .Select(row => new InsightDlc(Int(row[1]), Int(row[4])))
            .Distinct()
            .ToList();

        var statuses = (await Query("SELECT steam_id, app_id, status, updated_at, completed_at, price, currency FROM v_library_status WHERE status IS NOT NULL OR price IS NOT NULL").ConfigureAwait(false))
            .Where(row => scope.Includes(Text(row[0])))
            .Select(row => new InsightGameStatus(Int(row[1]), Text(row[2]) ?? string.Empty, Long(row[3]), NullableLong(row[4]), NullableDouble(row[5]), Text(row[6])))
            .ToList();

        var acquisitions = (await Query("SELECT steam_id, app_id, acquired_at FROM v_library_acquisition WHERE app_id > 0").ConfigureAwait(false))
            .Where(row => scope.Includes(Text(row[0])))
            .Select(row => new InsightAcquisition(Int(row[1]), Long(row[2])))
            .ToList();

        var builds = (await Query("SELECT app_id, build_id, observed_at, is_baseline FROM v_app_build ORDER BY observed_at").ConfigureAwait(false))
            .Select(row => new InsightBuildChange(Int(row[0]), Int(row[1]), Long(row[2]), Int(row[3]) != 0))
            .ToList();

        var schemaChanges = (await Query("SELECT app_id, detected_at FROM v_achievement_change").ConfigureAwait(false))
            .Select(row => new InsightSchemaChange(Int(row[0]), Long(row[1])))
            .ToList();

        var crashRecords = (await Query("SELECT record_id, steam_id, app_id, start_time, end_time, duration_seconds, build_id FROM v_crash_record ORDER BY start_time").ConfigureAwait(false))
            .Where(row => scope.Includes(Text(row[1])))
            .Select(row => new TagCandidate(Long(row[0]), Text(row[1]) ?? string.Empty, Int(row[2]), Long(row[3]), Long(row[4]), Long(row[5]), 0, NullableInt(row[6])))
            .ToList();
        var crashes = CrashDetector.WithUpdateSuspicion(CrashDetector.Detect(crashRecords).Select(item => item.Burst), builds);

        var friendRows = (await Query("""
                SELECT steam_id, friend_steam_id, friend_persona_name, change_type, previous_value, current_value, observed_at
                FROM v_friend_event
                WHERE observed_at >= $from AND change_type IN ('state', 'game', 'relationship', 'personaName')
                ORDER BY observed_at
                """, new Dictionary<string, object?> { ["$from"] = friendsFrom }).ConfigureAwait(false))
            .Where(row => scope.Includes(Text(row[0])))
            .Select(row => new FriendEventRow(Text(row[1]) ?? string.Empty, Text(row[2]) ?? string.Empty, Text(row[3]) ?? string.Empty, Text(row[4]), Text(row[5]), Long(row[6])));
        var friends = FriendTimeline.Build(friendRows);

        var owned = await catalog.GetOwnedGamesAsync(scope, cancellationToken).ConfigureAwait(false);
        var reconciliation = await catalog.GetReconciliationAsync(cancellationToken).ConfigureAwait(false);
        var appIds = sessions.Select(session => session.AppId).Concat(owned.Select(game => game.AppId)).ToHashSet();

        return new InsightArchive
        {
            Now = now,
            Options = options,
            Sessions = sessions,
            Unlocks = unlocks,
            Progress = progress,
            SetCompletions = setCompletions,
            Dlcs = dlcs,
            Owned = owned,
            Statuses = statuses,
            Acquisitions = acquisitions,
            Builds = builds,
            SchemaChanges = schemaChanges,
            Crashes = crashes,
            Apps = catalog.GetAppFacts(appIds),
            Friends = friends,
            Reconciliation = reconciliation
        };
    }

    private static string? Text(object? value) => value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);

    private static long Long(object? value) => value == null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    private static int Int(object? value) => value == null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);

    private static long? NullableLong(object? value) => value == null ? null : Long(value);

    private static int? NullableInt(object? value) => value == null ? null : Int(value);

    private static double? NullableDouble(object? value) => value == null ? null : Convert.ToDouble(value, CultureInfo.InvariantCulture);
}
