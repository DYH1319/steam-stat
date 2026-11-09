using System.Data.Common;
using System.Text.Json;
using SteamStat.Core.Features;

namespace ElectronNet.Features.DataTransfer;

/// <summary>完整 JSON 导出的头部信息。</summary>
internal sealed record DatabaseJsonMetadata(string AppVersion, long ExportedAt, string? Migration, string TimeZone, string? DeviceId);

/// <summary>
/// 完整 JSON 导出：按 SQLite 表原样导出所有档案数据（列名与库中一致，时间为 Unix 秒），
/// 供迁移或二次开发使用。Steam 凭证、资源缓存、运行时状态与迁移记录不导出；
/// 新增的表会自动包含在内。逐行流式写出，不在内存中保留整张表。
/// </summary>
internal static class DatabaseJsonExporter
{
    internal const string Format = "steam-stat-export";
    internal const int SchemaVersion = 1;

    /// <summary>永远不进入导出文件的表。</summary>
    internal static readonly IReadOnlySet<string> ExcludedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "steam_login_token",
        "steam_resource_cache",
        "global_status",
        "semantic_config",
        "semantic_app_filter",
        "__EFMigrationsHistory"
    };

    internal static async Task<IReadOnlyList<ExportTableResult>> WriteAsync(
        Stream output,
        DbConnection connection,
        DatabaseJsonMetadata metadata,
        IProgress<TaskProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var tables = await ListTablesAsync(connection, cancellationToken).ConfigureAwait(false);
        var results = new List<ExportTableResult>(tables.Count);

        await using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = false });
        writer.WriteStartObject();
        writer.WriteString("format", Format);
        writer.WriteNumber("schemaVersion", SchemaVersion);
        writer.WriteString("appVersion", metadata.AppVersion);
        writer.WriteNumber("exportedAt", metadata.ExportedAt);
        if (metadata.Migration != null) writer.WriteString("migration", metadata.Migration);
        writer.WriteString("timeZone", metadata.TimeZone);
        if (metadata.DeviceId != null) writer.WriteString("deviceId", metadata.DeviceId);
        writer.WriteStartObject("tables");

        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new TaskProgressUpdate("tables", results.Count, tables.Count));
            writer.WriteStartArray(table);
            var rows = 0;
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"SELECT * FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    writer.WriteStartObject();
                    for (var i = 0; i < names.Length; i++)
                    {
                        writer.WritePropertyName(names[i]);
                        WriteValue(writer, reader.IsDBNull(i) ? null : reader.GetValue(i));
                    }
                    writer.WriteEndObject();
                    rows++;
                    if (writer.BytesPending > 64 * 1024) await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            writer.WriteEndArray();
            results.Add(new ExportTableResult(table, rows, false));
        }

        writer.WriteEndObject();
        writer.WriteStartObject("counts");
        foreach (var result in results) writer.WriteNumber(result.Id, result.Rows);
        writer.WriteEndObject();
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        return results;
    }

    internal static async Task<IReadOnlyList<string>> ListTablesAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var tables = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            if (!ExcludedTables.Contains(name) && !name.StartsWith("sync_", StringComparison.OrdinalIgnoreCase)) tables.Add(name);
        }
        return tables;
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case long l: writer.WriteNumberValue(l); break;
            case int i: writer.WriteNumberValue(i); break;
            case double d when double.IsFinite(d): writer.WriteNumberValue(d); break;
            case double: writer.WriteNullValue(); break;
            case byte[] bytes: writer.WriteBase64StringValue(bytes); break;
            case bool b: writer.WriteBooleanValue(b); break;
            default: writer.WriteStringValue(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)); break;
        }
    }
}
