using Microsoft.Data.Sqlite;

namespace ElectronNet.Persistence;

/// <summary>直接操作 SQLite 文件时用到的小工具（备份副本、恢复、同步触发器）。表名 / 列名只来自代码常量或 sqlite_master。</summary>
internal static class SqliteSchema
{
    internal static async Task<bool> TableExistsAsync(SqliteConnection connection, string table, CancellationToken cancellationToken = default, string schema = "main")
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {Quote(schema)}.sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) > 0;
    }

    internal static async Task<IReadOnlyList<string>> ColumnsAsync(SqliteConnection connection, string table, CancellationToken cancellationToken = default, string schema = "main")
    {
        var columns = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {Quote(schema)}.table_info({Quote(table)})";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) columns.Add(reader.GetString(1));
        return columns;
    }

    internal static async Task<int> ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
    }

    internal static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
