using ElectronNet.Persistence;
using Microsoft.Data.Sqlite;

namespace ElectronNet.Features.Sync;

internal enum SyncApplyMode
{
    /// <summary>按同步键整行 upsert。</summary>
    Normal,
    /// <summary>设备表：只补充 / 改名，永远不改本机的 is_current。</summary>
    Device,
    /// <summary>应用元数据：只写名称、类型、开发商等，不碰安装状态；不用空值覆盖已有值。</summary>
    AppMetadata,
    /// <summary>资料变化：同一变化可能被多台设备各记一条，相近时间（±1 天）的相同变化视为同一条。</summary>
    ProfileHistory
}

/// <summary>
/// 一张参与同步的表。KeyColumns 为 ["sync_uid"] 时按全局唯一 ID 合并（事件类数据：会话、资料变化），
/// 否则按自然键合并（成就、游戏库等每个账号 + 应用只有一条的数据）。
/// 列名都是代码常量，可以直接拼进 SQL。
/// </summary>
internal sealed record SyncTableSpec(
    string Table,
    IReadOnlyList<string> KeyColumns,
    IReadOnlyList<string> Columns,
    bool PropagateDeletes,
    SyncApplyMode Mode = SyncApplyMode.Normal,
    /// <summary>只有满足条件的行才同步（例如只同步已结束的会话）。用 {row} 代表行别名。</summary>
    string? CaptureFilter = null,
    /// <summary>只有这些列变化才记为更新（默认为全部同步列）。</summary>
    IReadOnlyList<string>? ChangeColumns = null)
{
    internal bool IsUid => KeyColumns is ["sync_uid"];

    internal string KeyExpression(string alias)
        => IsUid ? $"{alias}.sync_uid" : $"json_array({string.Join(", ", KeyColumns.Select(column => $"{alias}.{column}"))})";

    internal string? Filter(string alias) => CaptureFilter?.Replace("{row}", alias, StringComparison.Ordinal);
}

/// <summary>
/// 同步范围（第十节：会话 + 成就 / 游戏库 / 资料档案）与变更捕获触发器。
/// Steam 凭证、好友记录、截图索引、存档备份与本地安装状态不参与同步。
/// 触发器在运行时幂等安装（不放进 EF 迁移）：以后的迁移若重建了某张表，下次启动会自动补回。
/// </summary>
internal static class SyncSchema
{
    internal const string TriggerPrefix = "sync_v1_";

    internal static readonly IReadOnlyList<SyncTableSpec> Tables =
    [
        new("device", ["id"], ["id", "name", "created_at"], PropagateDeletes: false, SyncApplyMode.Device, ChangeColumns: ["name"]),
        new("steam_app", ["app_id"],
            ["app_id", "name", "name_localized", "type", "developer", "publisher", "steam_release_date", "is_free_app"],
            PropagateDeletes: false, SyncApplyMode.AppMetadata),
        new("use_app_record", ["sync_uid"],
            ["sync_uid", "app_id", "steam_id", "start_time", "end_time", "duration", "last_heartbeat", "idle_duration",
             "foreground_duration", "device_id", "source", "end_reason", "note", "tags", "updated_at"],
            PropagateDeletes: true, CaptureFilter: "{row}.end_time IS NOT NULL",
            ChangeColumns: ["app_id", "steam_id", "start_time", "end_time", "duration", "idle_duration", "foreground_duration",
                            "device_id", "source", "end_reason", "note", "tags", "updated_at", "sync_uid"]),
        new("achievement_unlock_archive", ["account_name", "app_id", "internal_name"],
            ["account_name", "steam_id", "app_id", "internal_name", "name", "description", "icon", "hidden", "global_percent", "unlock_time", "recorded_at", "group_id", "archived"],
            PropagateDeletes: false,
            ChangeColumns: ["steam_id", "name", "description", "icon", "hidden", "unlock_time", "group_id", "archived"]),
        new("achievement_game_archive", ["account_name", "app_id"],
            ["account_name", "steam_id", "app_id", "app_name", "total", "unlocked", "progress_total", "progress_unlocked", "all_total", "all_unlocked", "synced_at"],
            PropagateDeletes: false,
            ChangeColumns: ["steam_id", "app_name", "total", "unlocked", "progress_total", "progress_unlocked", "all_total", "all_unlocked"]),
        new("achievement_set_archive", ["account_name", "app_id", "group_id"],
            ["account_name", "steam_id", "app_id", "group_id", "name", "dlc_app_id", "archived", "counted", "owned", "total", "unlocked", "all_total", "completed", "completed_at", "synced_at"],
            PropagateDeletes: false,
            ChangeColumns: ["name", "dlc_app_id", "archived", "counted", "owned", "total", "unlocked", "all_total", "completed", "completed_at"]),
        new("achievement_schema_change", ["app_id", "internal_name", "kind", "schema_hash"],
            ["app_id", "schema_hash", "internal_name", "name", "kind", "old_value", "new_value", "detected_at"],
            PropagateDeletes: false,
            ChangeColumns: ["name"]),
        new("achievement_target", ["account_name", "app_id", "internal_name"],
            ["account_name", "app_id", "app_name", "internal_name", "name", "description", "icon", "note", "created_at"],
            PropagateDeletes: true),
        new("library_acquisition", ["account_name", "package_id", "app_id"],
            ["account_name", "app_id", "package_id", "acquired_at"], PropagateDeletes: false),
        new("library_game_meta", ["account_name", "app_id"],
            ["account_name", "app_id", "status", "completed_at", "rating", "price", "currency", "note", "updated_at"],
            PropagateDeletes: true),
        new("profile_history", ["sync_uid"],
            ["sync_uid", "steam_id", "field", "value", "previous_value", "observed_at", "source"],
            PropagateDeletes: false, SyncApplyMode.ProfileHistory),
        new("playtime_snapshot", ["steam_id", "app_id", "playtime_minutes", "source"],
            ["steam_id", "app_id", "playtime_minutes", "source", "first_observed_at", "last_observed_at", "last_played_at"],
            PropagateDeletes: false)
    ];

    internal static readonly IReadOnlyDictionary<string, SyncTableSpec> ByName = Tables.ToDictionary(spec => spec.Table, StringComparer.Ordinal);

    private const string Guard = "(SELECT enabled = 1 AND applying = 0 FROM sync_control WHERE id = 1)";
    private const string NowMs = "CAST(ROUND((julianday('now') - 2440587.5) * 86400000) AS INTEGER)";

    /// <summary>安装（缺失时）全部触发器；表不存在时跳过（例如尚未迁移的旧库）。</summary>
    internal static async Task EnsureTriggersAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        if (!await SqliteSchema.TableExistsAsync(connection, "sync_control", cancellationToken).ConfigureAwait(false)) return;
        await DropStaleUpdateTriggersAsync(connection, cancellationToken).ConfigureAwait(false);
        foreach (var statement in TriggerStatements())
        {
            var table = statement.Table;
            if (!await SqliteSchema.TableExistsAsync(connection, table, cancellationToken).ConfigureAwait(false)) continue;
            await SqliteSchema.ExecuteAsync(connection, statement.Sql, cancellationToken).ConfigureAwait(false);
        }

        // 触发器安装之前（或某次迁移重建表、触发器暂时缺失期间）插入的行补齐同步 ID
        foreach (var spec in Tables.Where(spec => spec.IsUid))
        {
            if (!await SqliteSchema.TableExistsAsync(connection, spec.Table, cancellationToken).ConfigureAwait(false)) continue;
            await SqliteSchema.ExecuteAsync(connection,
                $"UPDATE {spec.Table} SET sync_uid = lower(hex(randomblob(16))) WHERE sync_uid IS NULL", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 更新触发器写死了列名，表加列后旧版本还在，新列的变化不会同步，要重建。
    /// 这里每次打开连接都会运行，所以只删确实过期的：无条件重建既抢写锁，又会留下没有触发器的窗口。
    /// </summary>
    private static async Task DropStaleUpdateTriggersAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var installed = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT name, sql FROM sqlite_master WHERE type = 'trigger' AND name LIKE '{TriggerPrefix}%'";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                installed[reader.GetString(0)] = reader.GetString(1);
        }
        foreach (var spec in Tables)
        {
            var name = $"{TriggerPrefix}{spec.Table}_au";
            if (!installed.TryGetValue(name, out var sql)) continue;
            var columns = spec.ChangeColumns ?? spec.Columns;
            if (columns.All(column => sql.Contains($"NEW.{column} IS NOT OLD.{column}", StringComparison.Ordinal))) continue;
            await SqliteSchema.ExecuteAsync(connection, $"DROP TRIGGER {name}", cancellationToken).ConfigureAwait(false);
        }
    }

    internal static IEnumerable<(string Table, string Sql)> TriggerStatements()
    {
        foreach (var spec in Tables)
        {
            var t = spec.Table;
            if (spec.IsUid)
            {
                // 插入时生成全局唯一 ID（与是否启用同步无关，远端写入的行自带 ID 不会触发）
                yield return (t, $"""
                    CREATE TRIGGER IF NOT EXISTS {TriggerPrefix}{t}_uid AFTER INSERT ON {t}
                    WHEN NEW.sync_uid IS NULL
                    BEGIN
                      UPDATE {t} SET sync_uid = lower(hex(randomblob(16))) WHERE rowid = NEW.rowid;
                    END
                    """);
            }

            var insertFilter = spec.Filter("NEW") is { } nf ? $" AND {nf}" : string.Empty;
            yield return (t, $"""
                CREATE TRIGGER IF NOT EXISTS {TriggerPrefix}{t}_ai AFTER INSERT ON {t}
                WHEN {Guard}{insertFilter}
                BEGIN
                  INSERT INTO sync_outbox (table_name, row_id, sync_key, op, changed_at) VALUES ('{t}', NEW.rowid, NULL, 'upsert', {NowMs});
                END
                """);

            var changeColumns = spec.ChangeColumns ?? spec.Columns;
            var changed = string.Join(" OR ", changeColumns.Select(column => $"NEW.{column} IS NOT OLD.{column}"));
            yield return (t, $"""
                CREATE TRIGGER IF NOT EXISTS {TriggerPrefix}{t}_au AFTER UPDATE ON {t}
                WHEN {Guard}{insertFilter} AND ({changed})
                BEGIN
                  INSERT INTO sync_outbox (table_name, row_id, sync_key, op, changed_at) VALUES ('{t}', NEW.rowid, NULL, 'upsert', {NowMs});
                END
                """);

            if (spec.PropagateDeletes)
            {
                var deleteFilter = spec.Filter("OLD") is { } of ? $" AND {of}" : string.Empty;
                yield return (t, $"""
                    CREATE TRIGGER IF NOT EXISTS {TriggerPrefix}{t}_ad AFTER DELETE ON {t}
                    WHEN {Guard}{deleteFilter}
                    BEGIN
                      INSERT INTO sync_outbox (table_name, row_id, sync_key, op, changed_at) VALUES ('{t}', OLD.rowid, {spec.KeyExpression("OLD")}, 'delete', {NowMs});
                    END
                    """);
            }
        }
    }
}
