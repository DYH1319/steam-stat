using System.Text.RegularExpressions;
using ElectronNet.Persistence;
using Microsoft.Data.Sqlite;
using SteamStat.Core.Settings;

namespace ElectronNet.Features.Semantic;

/// <summary>视图的隐私分级，与 AI / MCP 授权开关一一对应。</summary>
internal static class SemanticTier
{
    /// <summary>不含游戏名的聚合数字。</summary>
    internal const string Aggregate = "aggregate";
    /// <summary>游戏名、会话明细、成就。</summary>
    internal const string Games = "games";
    /// <summary>用户自己写的备注和标签。</summary>
    internal const string Notes = "notes";
    /// <summary>好友昵称和好友动态。</summary>
    internal const string Friends = "friends";

    internal static readonly IReadOnlyList<string> All = [Aggregate, Games, Notes, Friends];
}

internal sealed record SemanticView(string Name, string Tier, string Sql);

/// <summary>
/// 语义视图层：对外（MCP、命令行、OBS、封存包）只暴露这里的 v_* 视图，内部表可以继续改。
/// <para>
/// 视图和两张配置表都在运行时幂等安装，不放进 EF 迁移：EF 重建表时会 DROP 再 RENAME，
/// SQLite 校验到引用旧表的视图时 RENAME 会失败，所以迁移前要先 <see cref="DropViewsAsync"/>，迁移后再装回来。
/// </para>
/// <para>视图不能带参数，逻辑日分界点和隐藏游戏名单由 Host 写进 semantic_config / semantic_app_filter。</para>
/// </summary>
internal static partial class SemanticSchema
{
    /// <summary>公开契约的版本。删改已有列时才加一，新增视图和列不用。</summary>
    internal const int ContractVersion = 1;

    internal const string ConfigTable = "semantic_config";
    internal const string AppFilterTable = "semantic_app_filter";

    private const string Boundary = "(SELECT day_boundary_hour * 3600 FROM semantic_config)";
    private const string NotFiltered = "NOT IN (SELECT app_id FROM semantic_app_filter)";

    private static string LogicalDay(string column)
        => $"date({column} - {Boundary}, 'unixepoch', 'localtime')";

    /// <summary>Steam 账号名只用于关联，绝不进入视图列。</summary>
    private const string SteamIdOfAccount = "(SELECT u.steam_id FROM steam_user u WHERE u.account_name = {0} LIMIT 1)";

    internal static readonly IReadOnlyList<SemanticView> Views =
    [
        // 与 SessionCleaner 同一套规则：剔除 → 按间隔合并相邻会话 → 过滤过短会话
        new("v_session", SemanticTier.Games, $"""
            WITH base AS (
              SELECT r.id, r.steam_id, r.app_id, r.start_time, r.end_time, r.duration,
                     r.idle_duration, r.foreground_duration, r.source, r.end_reason, r.device_id
              FROM use_app_record r
              WHERE r.duration > 0 AND r.end_time IS NOT NULL AND r.app_id {NotFiltered}
            ),
            marked AS (
              SELECT b.*,
                     CASE WHEN c.merge_gap_seconds > 0
                               AND b.start_time - MAX(b.end_time) OVER previous <= c.merge_gap_seconds
                          THEN 0 ELSE 1 END AS starts_new
              FROM base b CROSS JOIN semantic_config c
              WINDOW previous AS (PARTITION BY b.steam_id, b.app_id ORDER BY b.start_time, b.id
                                  ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING)
            ),
            grouped AS (
              SELECT m.*, SUM(m.starts_new) OVER (PARTITION BY m.steam_id, m.app_id ORDER BY m.start_time, m.id) AS island
              FROM marked m
            ),
            merged AS (
              SELECT MIN(id) AS session_id, steam_id, app_id,
                     MIN(start_time) AS start_time, MAX(end_time) AS end_time,
                     SUM(duration) AS duration_seconds,
                     MAX(0, MIN(SUM(idle_duration), SUM(duration))) AS idle_seconds,
                     CASE WHEN COUNT(foreground_duration) = COUNT(*) THEN SUM(foreground_duration) END AS foreground_seconds,
                     COUNT(*) AS record_count,
                     CASE WHEN COUNT(DISTINCT source) = 1 THEN MIN(source) ELSE 'mixed' END AS source,
                     MIN(device_id) AS device_id,
                     SUM(end_reason = 'recovered') AS recovered_records
              FROM grouped
              GROUP BY steam_id, app_id, island
              HAVING SUM(duration) >= (SELECT min_duration_seconds FROM semantic_config)
            )
            SELECT s.session_id, s.steam_id, s.app_id,
                   (SELECT a.name FROM steam_app a WHERE a.app_id = s.app_id LIMIT 1) AS app_name,
                   s.start_time, s.end_time, s.duration_seconds, s.idle_seconds,
                   s.duration_seconds - s.idle_seconds AS effective_seconds,
                   s.foreground_seconds, s.record_count, s.source, s.device_id, s.recovered_records,
                   {LogicalDay("s.start_time")} AS logical_day
            FROM merged s
            """),

        // 与渲染层 splitByDay 一致：跨越分界点的会话拆段，挂机时长按比例分配
        new("v_session_day", SemanticTier.Games, $"""
            WITH RECURSIVE piece(session_id, steam_id, app_id, total, idle, cursor, stop, next_boundary) AS (
              SELECT s.session_id, s.steam_id, s.app_id, s.duration_seconds, s.idle_seconds,
                     s.start_time, s.start_time + s.duration_seconds,
                     CAST(strftime('%s', date(s.start_time - {Boundary}, 'unixepoch', 'localtime', '+1 day'), 'utc') AS INTEGER) + {Boundary}
              FROM v_session s
              UNION ALL
              SELECT p.session_id, p.steam_id, p.app_id, p.total, p.idle,
                     p.next_boundary, p.stop,
                     CAST(strftime('%s', date(p.next_boundary - {Boundary}, 'unixepoch', 'localtime', '+1 day'), 'utc') AS INTEGER) + {Boundary}
              FROM piece p
              WHERE p.next_boundary < p.stop
            )
            SELECT session_id, steam_id, app_id,
                   {LogicalDay("cursor")} AS logical_day,
                   MIN(stop, next_boundary) - cursor AS seconds,
                   idle * 1.0 * (MIN(stop, next_boundary) - cursor) / total AS idle_seconds,
                   (MIN(stop, next_boundary) - cursor) - idle * 1.0 * (MIN(stop, next_boundary) - cursor) / total AS effective_seconds
            FROM piece
            """),

        new("v_daily_playtime", SemanticTier.Aggregate, """
            SELECT logical_day, steam_id,
                   SUM(seconds) AS seconds, SUM(idle_seconds) AS idle_seconds, SUM(effective_seconds) AS effective_seconds,
                   COUNT(DISTINCT session_id) AS session_count, COUNT(DISTINCT app_id) AS app_count
            FROM v_session_day
            GROUP BY logical_day, steam_id
            """),

        new("v_app_playtime", SemanticTier.Games, """
            SELECT steam_id, app_id, MIN(app_name) AS app_name,
                   SUM(duration_seconds) AS seconds, SUM(idle_seconds) AS idle_seconds, SUM(effective_seconds) AS effective_seconds,
                   COUNT(*) AS session_count, MAX(duration_seconds) AS longest_session_seconds,
                   MIN(start_time) AS first_played, MAX(end_time) AS last_played
            FROM v_session
            GROUP BY steam_id, app_id
            """),

        new("v_app", SemanticTier.Games, $"""
            SELECT app_id, name, type, developer, publisher, steam_release_date AS release_date,
                   is_free_app AS is_free, installed
            FROM steam_app
            WHERE app_id {NotFiltered}
            """),

        new("v_user", SemanticTier.Aggregate, """
            SELECT steam_id, level FROM steam_user
            """),

        new("v_user_name", SemanticTier.Friends, """
            SELECT steam_id, persona_name FROM steam_user
            """),

        new("v_official_playtime", SemanticTier.Games, $"""
            SELECT steam_id, app_id, playtime_minutes, source,
                   first_observed_at, last_observed_at, last_played_at
            FROM playtime_snapshot
            WHERE app_id {NotFiltered}
            """),

        new("v_achievement_unlock", SemanticTier.Games, $"""
            SELECT steam_id, app_id, internal_name, name, description, hidden, global_percent, unlock_time,
                   {LogicalDay("unlock_time")} AS logical_day
            FROM achievement_unlock_archive
            WHERE unlock_time > 0 AND app_id {NotFiltered}
            """),

        new("v_achievement_game", SemanticTier.Games, $"""
            SELECT steam_id, app_id, app_name, total, unlocked, progress_total, progress_unlocked, synced_at
            FROM achievement_game_archive
            WHERE app_id {NotFiltered}
            """),

        new("v_library_status", SemanticTier.Games, $"""
            SELECT {string.Format(SteamIdOfAccount, "m.account_name")} AS steam_id,
                   m.app_id, m.status, m.completed_at, m.rating, m.updated_at
            FROM library_game_meta m
            WHERE m.app_id {NotFiltered}
            """),

        new("v_library_acquisition", SemanticTier.Games, $"""
            SELECT {string.Format(SteamIdOfAccount, "l.account_name")} AS steam_id,
                   l.app_id, l.package_id, l.acquired_at,
                   {LogicalDay("l.acquired_at")} AS logical_day
            FROM library_acquisition l
            WHERE l.app_id {NotFiltered}
            """),

        new("v_screenshot", SemanticTier.Games, $"""
            SELECT steam_id, app_id, taken_at, width, height, missing_since,
                   {LogicalDay("taken_at")} AS logical_day
            FROM screenshot_archive
            WHERE app_id {NotFiltered}
            """),

        new("v_profile_change", SemanticTier.Games, """
            SELECT steam_id, field, value, previous_value, observed_at, source
            FROM profile_history
            """),

        new("v_session_note", SemanticTier.Notes, $"""
            SELECT r.id AS record_id, r.steam_id, r.app_id, r.start_time, r.note, r.tags,
                   {LogicalDay("r.start_time")} AS logical_day
            FROM use_app_record r
            WHERE (r.note IS NOT NULL OR r.tags IS NOT NULL) AND r.app_id {NotFiltered}
            """),

        new("v_friend_event", SemanticTier.Friends, $"""
            SELECT {string.Format(SteamIdOfAccount, "f.account_name")} AS steam_id,
                   f.friend_steam_id, f.friend_persona_name, f.change_type,
                   f.previous_value, f.current_value, f.timestamp AS observed_at,
                   {LogicalDay("f.timestamp")} AS logical_day
            FROM friend_status_record f
            """),

        new("v_rich_presence", SemanticTier.Friends, $"""
            SELECT {string.Format(SteamIdOfAccount, "p.account_name")} AS observer_steam_id,
                   p.steam_id, p.is_self, p.app_id, p.display_text, p.raw_values, p.source, p.observed_at,
                   {LogicalDay("p.observed_at")} AS logical_day
            FROM rich_presence_record p
            WHERE p.app_id {NotFiltered}
            """)
    ];

    internal static readonly IReadOnlyDictionary<string, SemanticView> ByName
        = Views.ToDictionary(view => view.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>视图 SQL 实际读取的底层表。新视图用到新表时这里自动包含，不需要另外登记。</summary>
    internal static IReadOnlySet<string> UnderlyingTables()
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ConfigTable, AppFilterTable };
        foreach (var view in Views)
            foreach (var match in TableReference().EnumerateMatches(view.Sql))
            {
                var name = view.Sql.AsSpan(match.Index, match.Length).ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];
                tables.Add(name);
            }
        tables.ExceptWith(Views.Select(view => view.Name));
        tables.ExceptWith(["base", "marked", "grouped", "merged", "piece"]);
        return tables;
    }

    [GeneratedRegex(@"(?:FROM|JOIN)\s+[a-z_]+")]
    private static partial Regex TableReference();

    /// <summary>删除全部视图。迁移重建表之前必须先做。</summary>
    internal static async Task DropViewsAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        // 倒序删除：后面的视图引用前面的
        foreach (var view in Views.Reverse())
            await SqliteSchema.ExecuteAsync(connection, $"DROP VIEW IF EXISTS {view.Name}", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>重建配置表和全部视图，并写入当前口径。幂等，每次启动都执行。</summary>
    internal static async Task InstallAsync(
        SqliteConnection connection,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SqliteSchema.ExecuteAsync(connection, $"""
            CREATE TABLE IF NOT EXISTS {ConfigTable} (
              id INTEGER PRIMARY KEY CHECK (id = 1),
              day_boundary_hour INTEGER NOT NULL DEFAULT 4,
              week_start INTEGER NOT NULL DEFAULT 1,
              min_duration_seconds INTEGER NOT NULL DEFAULT 0,
              merge_gap_seconds INTEGER NOT NULL DEFAULT 0
            )
            """, cancellationToken).ConfigureAwait(false);
        await SqliteSchema.ExecuteAsync(connection, $"""
            CREATE TABLE IF NOT EXISTS {AppFilterTable} (
              app_id INTEGER NOT NULL,
              kind TEXT NOT NULL CHECK (kind IN ('hidden', 'excluded')),
              PRIMARY KEY (app_id, kind)
            ) WITHOUT ROWID
            """, cancellationToken).ConfigureAwait(false);
        await DropViewsAsync(connection, cancellationToken).ConfigureAwait(false);
        foreach (var view in Views)
            await SqliteSchema.ExecuteAsync(connection, $"CREATE VIEW {view.Name} AS\n{view.Sql}", cancellationToken).ConfigureAwait(false);
        await WriteConfigAsync(connection, settings, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>把设置里的口径写进配置表。调用方负责事务。</summary>
    internal static async Task WriteConfigAsync(SqliteConnection connection, AppSettings settings, CancellationToken cancellationToken = default)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                INSERT INTO {ConfigTable} (id, day_boundary_hour, week_start, min_duration_seconds, merge_gap_seconds)
                VALUES (1, $boundary, $week, $min, $gap)
                ON CONFLICT (id) DO UPDATE SET
                  day_boundary_hour = excluded.day_boundary_hour,
                  week_start = excluded.week_start,
                  min_duration_seconds = excluded.min_duration_seconds,
                  merge_gap_seconds = excluded.merge_gap_seconds
                """;
            command.Parameters.AddWithValue("$boundary", settings.Analytics?.DayBoundaryHour ?? 4);
            command.Parameters.AddWithValue("$week", settings.Analytics?.WeekStart ?? 1);
            command.Parameters.AddWithValue("$min", settings.SessionTracking?.MinDurationSeconds ?? 0);
            command.Parameters.AddWithValue("$gap", settings.SessionTracking?.MergeGapSeconds ?? 0);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await SqliteSchema.ExecuteAsync(connection, $"DELETE FROM {AppFilterTable}", cancellationToken).ConfigureAwait(false);
        await InsertFilterAsync(connection, "hidden", settings.Analytics?.HiddenAppIds, cancellationToken).ConfigureAwait(false);
        await InsertFilterAsync(connection, "excluded", settings.SessionTracking?.ExcludedAppIds, cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertFilterAsync(SqliteConnection connection, string kind, IEnumerable<int>? appIds, CancellationToken cancellationToken)
    {
        foreach (var appId in (appIds ?? []).Distinct())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"INSERT INTO {AppFilterTable} (app_id, kind) VALUES ($app, $kind)";
            command.Parameters.AddWithValue("$app", appId);
            command.Parameters.AddWithValue("$kind", kind);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
