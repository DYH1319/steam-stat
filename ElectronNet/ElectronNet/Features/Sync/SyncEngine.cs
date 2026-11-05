using System.Runtime.CompilerServices;
using System.Text.Json;
using ElectronNet.Persistence;
using Microsoft.Data.Sqlite;
using SteamStat.Core.Sync;

namespace ElectronNet.Features.Sync;

internal sealed record SyncApplyResult(int Applied, int Skipped);

/// <summary>
/// 同步引擎（只操作本机 SQLite，不涉及网络）：
/// <list type="bullet">
/// <item>导出：读取触发器写入的 outbox，或生成全量快照，并记录行版本</item>
/// <item>应用：按「最后写入胜出」（版本时间，相同时比较设备 ID）合并远端变更；删除保留为墓碑</item>
/// </list>
/// 应用时在同一个写事务里把 sync_control.applying 置 1，触发器不会把远端变更再记进 outbox。
/// </summary>
internal sealed class SyncEngine(string deviceId, TimeProvider timeProvider)
{
    internal const int ChunkSize = 5000;
    private const long ProfileHistoryWindowSeconds = 86_400;

    internal string DeviceId { get; } = deviceId;

    private long NowMs => timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    #region 导出

    /// <summary>读取 outbox（最多 <paramref name="limit"/> 条），合并同一行的多次变更。返回变更与已读到的最大 outbox 序号。</summary>
    internal async Task<(List<SyncChange> Changes, long MaxSeq)> ReadOutboxAsync(SqliteConnection connection, int limit = ChunkSize, CancellationToken cancellationToken = default)
    {
        var entries = new List<(long Seq, string Table, long RowId, string? Key, string Op, long At)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT seq, table_name, row_id, sync_key, op, changed_at FROM sync_outbox ORDER BY seq LIMIT $limit";
            command.Parameters.AddWithValue("$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                entries.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4), reader.GetInt64(5)));
            }
        }
        if (entries.Count == 0) return ([], 0);

        var latest = entries
            .Where(entry => SyncSchema.ByName.ContainsKey(entry.Table))
            .GroupBy(entry => (entry.Table, entry.RowId))
            .Select(group => group.MaxBy(entry => entry.Seq))
            .ToList();
        var changes = new List<SyncChange>();
        foreach (var tableGroup in latest.GroupBy(entry => entry.Table))
        {
            var spec = SyncSchema.ByName[tableGroup.Key];
            foreach (var delete in tableGroup.Where(entry => entry.Op == "delete" && entry.Key != null))
                changes.Add(new SyncChange { Table = spec.Table, Op = "delete", Key = delete.Key!, At = delete.At, Device = DeviceId });

            var upserts = tableGroup.Where(entry => entry.Op == "upsert").ToDictionary(entry => entry.RowId, entry => entry.At);
            foreach (var chunk in upserts.Keys.Chunk(500))
            {
                await foreach (var (rowId, key, row) in ReadRowsAsync(connection, spec, $"rowid IN ({string.Join(',', chunk)})", cancellationToken).ConfigureAwait(false))
                    changes.Add(new SyncChange { Table = spec.Table, Op = "upsert", Key = key, At = upserts[rowId], Device = DeviceId, Row = row });
            }
        }
        return (changes, entries[^1].Seq);
    }

    /// <summary>全量快照：所有同步行（带已有版本，没有版本的记为现在、本机）与墓碑，按块返回。</summary>
    internal async IAsyncEnumerable<List<SyncChange>> ReadSnapshotAsync(SqliteConnection connection, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var now = NowMs;
        var chunk = new List<SyncChange>(ChunkSize);
        foreach (var spec in SyncSchema.Tables)
        {
            if (!await SqliteSchema.TableExistsAsync(connection, spec.Table, cancellationToken).ConfigureAwait(false)) continue;
            var versions = await ReadVersionsAsync(connection, spec.Table, cancellationToken).ConfigureAwait(false);
            await foreach (var (_, key, row) in ReadRowsAsync(connection, spec, null, cancellationToken).ConfigureAwait(false))
            {
                var version = versions.GetValueOrDefault(key);
                chunk.Add(new SyncChange
                {
                    Table = spec.Table, Op = "upsert", Key = key, Row = row,
                    At = version.Deleted || version.At == 0 ? now : version.At,
                    Device = version.Deleted || version.At == 0 ? DeviceId : version.Device
                });
                if (chunk.Count >= ChunkSize)
                {
                    yield return chunk;
                    chunk = new List<SyncChange>(ChunkSize);
                }
            }
            foreach (var (key, version) in versions.Where(pair => pair.Value.Deleted))
            {
                chunk.Add(new SyncChange { Table = spec.Table, Op = "delete", Key = key, At = version.At, Device = version.Device });
                if (chunk.Count >= ChunkSize)
                {
                    yield return chunk;
                    chunk = new List<SyncChange>(ChunkSize);
                }
            }
        }
        if (chunk.Count > 0) yield return chunk;
    }

    /// <summary>上传成功后记录行版本（只在比已有版本新时更新）。</summary>
    internal static async Task RecordVersionsAsync(SqliteConnection connection, IReadOnlyList<SyncChange> changes, CancellationToken cancellationToken = default)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var change in changes)
        {
            await UpsertVersionAsync(connection, transaction, change, onlyIfNewer: true, cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task ClearOutboxAsync(SqliteConnection connection, long maxSeq, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sync_outbox WHERE seq <= $seq";
        command.Parameters.AddWithValue("$seq", maxSeq);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async IAsyncEnumerable<(long RowId, string Key, Dictionary<string, object?> Row)> ReadRowsAsync(
        SqliteConnection connection, SyncTableSpec spec, string? where, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var available = (await SqliteSchema.ColumnsAsync(connection, spec.Table, cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        var columns = spec.Columns.Where(available.Contains).ToList();
        var conditions = new[] { where, spec.Filter(spec.Table), spec.IsUid ? $"{spec.Table}.sync_uid IS NOT NULL" : null }.Where(condition => condition != null);
        var whereClause = string.Join(" AND ", conditions);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT rowid, {spec.KeyExpression(spec.Table)}, {string.Join(", ", columns)} FROM {spec.Table}"
                              + (whereClause.Length > 0 ? $" WHERE {whereClause}" : string.Empty)
                              + " ORDER BY rowid";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new Dictionary<string, object?>(columns.Count, StringComparer.Ordinal);
            for (var i = 0; i < columns.Count; i++)
            {
                var value = reader.IsDBNull(i + 2) ? null : reader.GetValue(i + 2);
                row[columns[i]] = value is byte[] ? null : value;
            }
            yield return (reader.GetInt64(0), reader.GetString(1), row);
        }
    }

    private static async Task<Dictionary<string, (long At, string Device, bool Deleted)>> ReadVersionsAsync(
        SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        var versions = new Dictionary<string, (long, string, bool)>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sync_key, changed_at, device_id, deleted FROM sync_version WHERE table_name = $table";
        command.Parameters.AddWithValue("$table", table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            versions[reader.GetString(0)] = (reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3) != 0);
        return versions;
    }

    #endregion

    #region 应用

    /// <summary>在一个写事务里应用一批远端变更。</summary>
    internal async Task<SyncApplyResult> ApplyAsync(SqliteConnection connection, IReadOnlyList<SyncChange> changes, Func<SqliteTransaction, Task>? beforeCommit = null, CancellationToken cancellationToken = default)
    {
        var columnCache = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var applied = 0;
        var skipped = 0;
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await Execute(connection, transaction, "UPDATE sync_control SET applying = 1 WHERE id = 1", cancellationToken).ConfigureAwait(false);
        foreach (var change in changes)
        {
            if (!SyncSchema.ByName.TryGetValue(change.Table, out var spec) || change.Key.Length is 0 or > 512 || change.Device.Length is 0 or > 64)
            {
                skipped++;
                continue;
            }
            if (!columnCache.TryGetValue(spec.Table, out var available))
            {
                if (!await SqliteSchema.TableExistsAsync(connection, spec.Table, cancellationToken).ConfigureAwait(false))
                {
                    skipped++;
                    continue;
                }
                available = (await SqliteSchema.ColumnsAsync(connection, spec.Table, cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
                columnCache[spec.Table] = available;
            }

            var keyValues = ParseKey(spec, change.Key);
            if (keyValues == null || !await IsNewerAsync(connection, transaction, spec, change, keyValues, cancellationToken).ConfigureAwait(false))
            {
                skipped++;
                continue;
            }

            if (change.Op == "delete")
            {
                if (spec.PropagateDeletes)
                {
                    await using var delete = connection.CreateCommand();
                    delete.Transaction = transaction;
                    delete.CommandText = $"DELETE FROM {spec.Table} WHERE {KeyPredicate(spec, delete, keyValues)}";
                    await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            else if (change.Row != null)
            {
                var values = spec.Columns
                    .Where(column => available.Contains(column) && change.Row.ContainsKey(column))
                    .ToDictionary(column => column, column => ToDbValue(change.Row[column]), StringComparer.Ordinal);
                for (var i = 0; i < spec.KeyColumns.Count; i++) values[spec.KeyColumns[i]] = keyValues[i];
                await UpsertRowAsync(connection, transaction, spec, keyValues, values, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                skipped++;
                continue;
            }
            await UpsertVersionAsync(connection, transaction, change, onlyIfNewer: false, cancellationToken).ConfigureAwait(false);
            applied++;
        }
        if (beforeCommit != null) await beforeCommit(transaction).ConfigureAwait(false);
        await Execute(connection, transaction, "UPDATE sync_control SET applying = 0 WHERE id = 1", cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SyncApplyResult(applied, skipped);
    }

    /// <summary>远端版本是否比本机新：先比已记录的版本，再比本机尚未上传的变更。</summary>
    private static async Task<bool> IsNewerAsync(SqliteConnection connection, SqliteTransaction transaction, SyncTableSpec spec, SyncChange change, object?[] keyValues, CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT changed_at, device_id FROM sync_version WHERE table_name = $table AND sync_key = $key";
            command.Parameters.AddWithValue("$table", spec.Table);
            command.Parameters.AddWithValue("$key", change.Key);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var at = reader.GetInt64(0);
                var device = reader.GetString(1);
                if (change.At < at || (change.At == at && string.CompareOrdinal(change.Device, device) <= 0)) return false;
            }
        }

        await using (var pending = connection.CreateCommand())
        {
            pending.Transaction = transaction;
            pending.CommandText = $"SELECT MAX(o.changed_at) FROM sync_outbox o WHERE o.table_name = $table AND (o.sync_key = $key OR o.row_id IN (SELECT rowid FROM {spec.Table} WHERE {KeyPredicate(spec, pending, keyValues)}))";
            pending.Parameters.AddWithValue("$table", spec.Table);
            pending.Parameters.AddWithValue("$key", change.Key);
            var result = await pending.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is long localAt && localAt >= change.At) return false;
        }
        return true;
    }

    private async Task UpsertRowAsync(SqliteConnection connection, SqliteTransaction transaction, SyncTableSpec spec, object?[] keyValues, Dictionary<string, object?> values, CancellationToken cancellationToken)
    {
        long? rowId;
        await using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = $"SELECT rowid FROM {spec.Table} WHERE {KeyPredicate(spec, find, keyValues)} LIMIT 1";
            rowId = await find.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as long?;
        }

        switch (spec.Mode)
        {
            case SyncApplyMode.Device:
                if (rowId != null)
                {
                    await Execute(connection, transaction, $"UPDATE device SET name = $name WHERE rowid = $rowid AND is_current = 0",
                        cancellationToken, ("$name", values.GetValueOrDefault("name") ?? DBNull.Value), ("$rowid", rowId)).ConfigureAwait(false);
                    return;
                }
                values["is_current"] = 0L;
                values.TryAdd("name", values["id"]);
                values.TryAdd("created_at", 0L);
                break;

            case SyncApplyMode.AppMetadata:
                // 空的本地化名称记为 {}：不用空值覆盖已有的元数据
                if (values.TryGetValue("name_localized", out var localized) && localized is "{}" or "") values["name_localized"] = DBNull.Value;
                if (rowId != null)
                {
                    var assignments = values.Keys.Where(column => column != "app_id")
                        .Select(column => $"{column} = COALESCE(${column}, {column})").ToList();
                    if (assignments.Count == 0) return;
                    await using var update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText = $"UPDATE steam_app SET {string.Join(", ", assignments)} WHERE rowid = $rowid";
                    foreach (var (column, value) in values) update.Parameters.AddWithValue($"${column}", value ?? DBNull.Value);
                    update.Parameters.AddWithValue("$rowid", rowId);
                    await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (values.GetValueOrDefault("name_localized") is null or DBNull) values["name_localized"] = "{}";
                values["installed"] = 0L;
                values["is_running"] = 0L;
                break;

            case SyncApplyMode.ProfileHistory when rowId == null:
                if (await HasNearbyProfileChangeAsync(connection, transaction, values, cancellationToken).ConfigureAwait(false)) return;
                break;
        }

        if (rowId != null)
        {
            var assignments = values.Keys.Select(column => $"{column} = ${column}").ToList();
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = $"UPDATE {spec.Table} SET {string.Join(", ", assignments)} WHERE rowid = $rowid";
            foreach (var (column, value) in values) update.Parameters.AddWithValue($"${column}", value ?? DBNull.Value);
            update.Parameters.AddWithValue("$rowid", rowId);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = $"INSERT INTO {spec.Table} ({string.Join(", ", values.Keys)}) VALUES ({string.Join(", ", values.Keys.Select(column => $"${column}"))})";
            foreach (var (column, value) in values) insert.Parameters.AddWithValue($"${column}", value ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>另一台设备在相近时间（±1 天）记下了同一次资料变化时不再重复插入。</summary>
    private static async Task<bool> HasNearbyProfileChangeAsync(SqliteConnection connection, SqliteTransaction transaction, Dictionary<string, object?> values, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1 FROM profile_history
            WHERE steam_id = $steam AND field = $field AND value = $value
              AND IFNULL(previous_value, '') = IFNULL($previous, '')
              AND ABS(observed_at - $observed) <= $window
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$steam", values.GetValueOrDefault("steam_id") ?? DBNull.Value);
        command.Parameters.AddWithValue("$field", values.GetValueOrDefault("field") ?? DBNull.Value);
        command.Parameters.AddWithValue("$value", values.GetValueOrDefault("value") ?? DBNull.Value);
        command.Parameters.AddWithValue("$previous", values.GetValueOrDefault("previous_value") ?? DBNull.Value);
        command.Parameters.AddWithValue("$observed", values.GetValueOrDefault("observed_at") ?? 0L);
        command.Parameters.AddWithValue("$window", ProfileHistoryWindowSeconds);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) != null;
    }

    private static async Task UpsertVersionAsync(SqliteConnection connection, SqliteTransaction transaction, SyncChange change, bool onlyIfNewer, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sync_version (table_name, sync_key, changed_at, device_id, deleted)
            VALUES ($table, $key, $at, $device, $deleted)
            ON CONFLICT (table_name, sync_key) DO UPDATE SET
              changed_at = excluded.changed_at, device_id = excluded.device_id, deleted = excluded.deleted
            """ + (onlyIfNewer
            ? " WHERE excluded.changed_at > sync_version.changed_at OR (excluded.changed_at = sync_version.changed_at AND excluded.device_id > sync_version.device_id)"
            : string.Empty);
        command.Parameters.AddWithValue("$table", change.Table);
        command.Parameters.AddWithValue("$key", change.Key);
        command.Parameters.AddWithValue("$at", change.At);
        command.Parameters.AddWithValue("$device", change.Device);
        command.Parameters.AddWithValue("$deleted", change.Op == "delete" ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>同步键 → 各键列的值；uid 键直接使用，自然键是 SQLite json_array 生成的 JSON 数组。</summary>
    internal static object?[]? ParseKey(SyncTableSpec spec, string key)
    {
        if (spec.IsUid) return key.Length is >= 8 and <= 64 && key.All(char.IsAsciiLetterOrDigit) ? [key] : null;
        try
        {
            using var document = JsonDocument.Parse(key);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() != spec.KeyColumns.Count) return null;
            return document.RootElement.EnumerateArray().Select(element => ToDbValue(element)).ToArray();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string KeyPredicate(SyncTableSpec spec, SqliteCommand command, object?[] keyValues)
    {
        var parts = new List<string>(spec.KeyColumns.Count);
        for (var i = 0; i < spec.KeyColumns.Count; i++)
        {
            var name = $"$k{i}";
            if (!command.Parameters.Contains(name)) command.Parameters.AddWithValue(name, keyValues[i] ?? DBNull.Value);
            parts.Add($"{spec.KeyColumns[i]} IS {name}");
        }
        return string.Join(" AND ", parts);
    }

    /// <summary>JSON 值 → SQLite 参数（整数 / 实数 / 文本 / 空）。</summary>
    internal static object? ToDbValue(object? value) => value switch
    {
        null => null,
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        JsonElement { ValueKind: JsonValueKind.Number } element => element.TryGetInt64(out var integer) ? integer : element.GetDouble(),
        JsonElement { ValueKind: JsonValueKind.True } => 1L,
        JsonElement { ValueKind: JsonValueKind.False } => 0L,
        JsonElement => null,
        bool flag => flag ? 1L : 0L,
        int number => (long)number,
        _ => value
    };

    private static async Task Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    #endregion
}
