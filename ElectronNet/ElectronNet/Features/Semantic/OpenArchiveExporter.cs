using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ElectronNet.Features.Semantic;

/// <summary>导出范围：逻辑日闭区间。Label 显示在静态页和说明文件里。</summary>
internal sealed record OpenArchiveScope(string FromDay, string ToDay, string Label);

internal sealed record OpenArchiveMetadata(string AppVersion, long CreatedAt, string TimeZone, int DayBoundaryHour);

internal sealed record OpenArchiveSummary(int SessionCount, long TotalSeconds, long EffectiveSeconds, int ActiveDays, int GameCount, int AchievementCount);

internal sealed record OpenArchiveDataset(string File, string Sql);

/// <summary>
/// 开放格式导出：CSV + JSON + 自述文件 + 不依赖脚本的静态页。应用停更后也能打开，所以这里不用任何专有格式。
/// 只经语义视图读数据，隐藏游戏自动不在其中；好友的数据是别人的，不进这个包。
/// 年度封存包用它，以后的自包含静态站也用它。
/// </summary>
internal static class OpenArchiveExporter
{
    internal const string Format = "steam-stat-open-archive";
    internal const int FormatVersion = 1;

    internal delegate Task<SemanticQueryResult> QueryRunner(string sql, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken);

    internal static readonly IReadOnlyList<OpenArchiveDataset> Datasets =
    [
        new("data/sessions.csv", """
            SELECT session_id, steam_id, app_id, app_name, start_time, end_time, duration_seconds, idle_seconds,
                   effective_seconds, source, record_count, logical_day
            FROM v_session WHERE logical_day BETWEEN $from AND $to ORDER BY start_time
            """),
        new("data/daily_playtime.csv", """
            SELECT logical_day, steam_id, seconds, idle_seconds, effective_seconds, session_count, app_count
            FROM v_daily_playtime WHERE logical_day BETWEEN $from AND $to ORDER BY logical_day
            """),
        new("data/games.csv", """
            SELECT d.steam_id, d.app_id, a.name, a.developer, a.publisher, SUM(d.seconds) AS seconds,
                   SUM(d.effective_seconds) AS effective_seconds, COUNT(DISTINCT d.session_id) AS session_count
            FROM v_session_day d LEFT JOIN v_app a ON a.app_id = d.app_id
            WHERE d.logical_day BETWEEN $from AND $to
            GROUP BY d.steam_id, d.app_id ORDER BY seconds DESC
            """),
        new("data/achievements.csv", """
            SELECT steam_id, app_id, internal_name, name, description, global_percent, unlock_time, logical_day
            FROM v_achievement_unlock WHERE logical_day BETWEEN $from AND $to ORDER BY unlock_time
            """),
        new("data/library_acquisitions.csv", """
            SELECT steam_id, app_id, package_id, acquired_at, logical_day
            FROM v_library_acquisition WHERE logical_day BETWEEN $from AND $to ORDER BY acquired_at
            """),
        new("data/screenshots.csv", """
            SELECT steam_id, app_id, taken_at, width, height, logical_day
            FROM v_screenshot WHERE logical_day BETWEEN $from AND $to ORDER BY taken_at
            """),
        new("data/session_notes.csv", """
            SELECT record_id, steam_id, app_id, start_time, note, tags, logical_day
            FROM v_session_note WHERE logical_day BETWEEN $from AND $to ORDER BY start_time
            """),
        new("data/rich_presence.csv", """
            SELECT app_id, display_text, raw_values, source, observed_at, logical_day
            FROM v_rich_presence WHERE is_self = 1 AND logical_day BETWEEN $from AND $to ORDER BY observed_at
            """)
    ];

    /// <summary>写入 zip 流，返回摘要。流由调用方管理。</summary>
    internal static async Task<OpenArchiveSummary> WriteAsync(
        Stream output,
        OpenArchiveScope scope,
        OpenArchiveMetadata metadata,
        QueryRunner query,
        CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?> { ["$from"] = scope.FromDay, ["$to"] = scope.ToDay };
        var manifest = new List<(string Name, long Size, string Sha256)>();
        var tables = new Dictionary<string, SemanticQueryResult>(StringComparer.Ordinal);

        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var dataset in Datasets)
        {
            var result = await query(dataset.Sql, parameters, cancellationToken).ConfigureAwait(false);
            if (result.Failure != SemanticQueryFailure.None)
                throw new InvalidOperationException($"Open archive query {dataset.File} failed: {result.Failure}");
            tables[dataset.File] = result;
            manifest.Add(AddEntry(zip, dataset.File, ToCsv(result)));
        }

        var summary = Summarize(tables);
        manifest.Add(AddEntry(zip, "data/archive.json", Encoding.UTF8.GetBytes(ToJson(tables, scope, metadata, summary))));
        manifest.Add(AddEntry(zip, "index.html", Encoding.UTF8.GetBytes(ToHtml(tables, scope, metadata, summary))));
        manifest.Add(AddEntry(zip, "README.md", Encoding.UTF8.GetBytes(ToReadme(scope, metadata, summary))));

        var manifestJson = new JsonObject
        {
            ["format"] = Format,
            ["version"] = FormatVersion,
            ["label"] = scope.Label,
            ["from"] = scope.FromDay,
            ["to"] = scope.ToDay,
            ["appVersion"] = metadata.AppVersion,
            ["createdAt"] = metadata.CreatedAt,
            ["timeZone"] = metadata.TimeZone,
            ["dayBoundaryHour"] = metadata.DayBoundaryHour,
            ["contractVersion"] = SemanticSchema.ContractVersion,
            ["files"] = new JsonArray(manifest.Select(file => (JsonNode)new JsonObject
            {
                ["name"] = file.Name,
                ["size"] = file.Size,
                ["sha256"] = file.Sha256
            }).ToArray())
        };
        AddEntry(zip, "manifest.json", Encoding.UTF8.GetBytes(manifestJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
        return summary;
    }

    private static (string Name, long Size, string Sha256) AddEntry(ZipArchive zip, string name, byte[] content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        // 固定时间戳：同样的内容得到同样的包
        entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using (var stream = entry.Open()) stream.Write(content);
        return (name, content.LongLength, Convert.ToHexStringLower(SHA256.HashData(content)));
    }

    private static long SumColumn(SemanticQueryResult table, string column)
    {
        var index = IndexOf(table, column);
        return table.Rows.Sum(row => row[index] == null ? 0L : (long)Math.Round(Convert.ToDouble(row[index], CultureInfo.InvariantCulture)));
    }

    private static int IndexOf(SemanticQueryResult table, string column) => table.Columns.ToList().IndexOf(column);

    private static OpenArchiveSummary Summarize(IReadOnlyDictionary<string, SemanticQueryResult> tables)
    {
        var daily = tables["data/daily_playtime.csv"];
        var games = tables["data/games.csv"];
        var day = IndexOf(daily, "logical_day");
        var app = IndexOf(games, "app_id");
        return new OpenArchiveSummary(
            tables["data/sessions.csv"].Rows.Count,
            SumColumn(daily, "seconds"),
            SumColumn(daily, "effective_seconds"),
            daily.Rows.Select(row => row[day]).Distinct().Count(),
            games.Rows.Select(row => row[app]).Distinct().Count(),
            tables["data/achievements.csv"].Rows.Count);
    }

    /// <summary>RFC 4180，UTF-8 带 BOM。文本里以公式字符开头的值加单引号，和数据导出的规则一致。</summary>
    internal static byte[] ToCsv(SemanticQueryResult table)
    {
        var builder = new StringBuilder("﻿");
        builder.Append(string.Join(',', table.Columns.Select(Escape))).Append("\r\n");
        foreach (var row in table.Rows)
        {
            builder.Append(string.Join(',', row.Select(FormatCell)));
            builder.Append("\r\n");
        }
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static string FormatCell(object? value) => value switch
    {
        null => string.Empty,
        string text => Escape(Neutralize(text)),
        double number => number.ToString("0.###", CultureInfo.InvariantCulture),
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => Escape(value.ToString() ?? string.Empty)
    };

    private static string Neutralize(string text)
        => text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + text : text;

    private static string Escape(string text)
        => text.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;

    private static string ToJson(
        IReadOnlyDictionary<string, SemanticQueryResult> tables,
        OpenArchiveScope scope,
        OpenArchiveMetadata metadata,
        OpenArchiveSummary summary)
    {
        var data = new JsonObject();
        foreach (var (file, table) in tables)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var rows = new JsonArray();
            foreach (var row in table.Rows)
            {
                var item = new JsonObject();
                for (var index = 0; index < table.Columns.Count; index++)
                    item[table.Columns[index]] = row[index] == null ? null : JsonValue.Create(row[index]!, null);
                rows.Add(item);
            }
            data[name] = rows;
        }
        return new JsonObject
        {
            ["format"] = Format,
            ["version"] = FormatVersion,
            ["label"] = scope.Label,
            ["from"] = scope.FromDay,
            ["to"] = scope.ToDay,
            ["appVersion"] = metadata.AppVersion,
            ["createdAt"] = metadata.CreatedAt,
            ["timeZone"] = metadata.TimeZone,
            ["dayBoundaryHour"] = metadata.DayBoundaryHour,
            ["summary"] = JsonSerializer.SerializeToNode(summary),
            ["data"] = data
        }.ToJsonString();
    }

    private static string Hours(long seconds) => (seconds / 3600.0).ToString("0.0", CultureInfo.InvariantCulture);

    private static string ToHtml(
        IReadOnlyDictionary<string, SemanticQueryResult> tables,
        OpenArchiveScope scope,
        OpenArchiveMetadata metadata,
        OpenArchiveSummary summary)
    {
        var games = tables["data/games.csv"];
        var nameIndex = IndexOf(games, "name");
        var appIndex = IndexOf(games, "app_id");
        var secondsIndex = IndexOf(games, "seconds");
        var top = games.Rows
            .GroupBy(row => row[appIndex])
            .Select(group => (
                Name: group.First()[nameIndex] as string ?? $"App {group.Key}",
                Seconds: group.Sum(row => Convert.ToInt64(row[secondsIndex], CultureInfo.InvariantCulture))))
            .OrderByDescending(item => item.Seconds)
            .Take(20)
            .ToList();
        var topMax = Math.Max(1, top.FirstOrDefault().Seconds);

        var daily = tables["data/daily_playtime.csv"];
        var dayIndex = IndexOf(daily, "logical_day");
        var dailySeconds = IndexOf(daily, "seconds");
        var months = daily.Rows
            .GroupBy(row => ((string)row[dayIndex]!)[..7])
            .Select(group => (Month: group.Key, Seconds: group.Sum(row => (long)Convert.ToDouble(row[dailySeconds], CultureInfo.InvariantCulture))))
            .OrderBy(item => item.Month)
            .ToList();
        var monthMax = Math.Max(1, months.Count == 0 ? 1 : months.Max(item => item.Seconds));

        var html = new StringBuilder();
        html.Append($$"""
            <!doctype html>
            <html lang="zh-CN">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Steam Stat · {{Encode(scope.Label)}}</title>
            <style>
            :root { color-scheme: light dark; --ink: #1c2530; --muted: #66727f; --paper: #fbfaf7; --line: #dcd9d1; --bar: #2f6f8f; }
            @media (prefers-color-scheme: dark) { :root { --ink: #e8e6e1; --muted: #9aa3ad; --paper: #14181d; --line: #2c333b; --bar: #6fb1cf; } }
            * { box-sizing: border-box; }
            body { margin: 0 auto; max-width: 760px; padding: 32px 16px 64px; background: var(--paper); color: var(--ink); font: 16px/1.6 system-ui, sans-serif; }
            h1 { margin: 0; font-size: 2.2rem; }
            h2 { margin: 2.4rem 0 .8rem; font-size: 1.1rem; }
            .meta { color: var(--muted); font-size: .85rem; }
            .figures { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 12px; margin-top: 1.6rem; }
            .figure { border-top: 2px solid var(--ink); padding-top: 6px; }
            .figure b { display: block; font-size: 1.6rem; font-variant-numeric: tabular-nums; }
            .figure span { color: var(--muted); font-size: .85rem; }
            .row { display: grid; grid-template-columns: minmax(0, 12rem) 1fr 4.5rem; gap: 10px; align-items: center; padding: 3px 0; }
            .row .label { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
            .row .value { text-align: right; font-variant-numeric: tabular-nums; color: var(--muted); }
            .track { background: var(--line); height: 8px; }
            .fill { background: var(--bar); height: 8px; }
            footer { margin-top: 3rem; border-top: 1px solid var(--line); padding-top: 1rem; color: var(--muted); font-size: .85rem; }
            </style>
            </head>
            <body>
            <h1>{{Encode(scope.Label)}}</h1>
            <p class="meta">{{Encode(scope.FromDay)}} – {{Encode(scope.ToDay)}}</p>
            <div class="figures">
            <div class="figure"><b>{{Hours(summary.TotalSeconds)}}</b><span>小时 · hours</span></div>
            <div class="figure"><b>{{summary.ActiveDays}}</b><span>活跃天数 · active days</span></div>
            <div class="figure"><b>{{summary.GameCount}}</b><span>款游戏 · games</span></div>
            <div class="figure"><b>{{summary.AchievementCount}}</b><span>个成就 · achievements</span></div>
            </div>
            <h2>时长最长的游戏 · Most played</h2>

            """);
        foreach (var item in top)
            html.Append($"""<div class="row"><span class="label">{Encode(item.Name)}</span><div class="track"><div class="fill" style="width:{item.Seconds * 100 / topMax}%"></div></div><span class="value">{Hours(item.Seconds)} h</span></div>""").Append('\n');
        html.Append("<h2>每月时长 · By month</h2>\n");
        foreach (var item in months)
            html.Append($"""<div class="row"><span class="label">{item.Month}</span><div class="track"><div class="fill" style="width:{item.Seconds * 100 / monthMax}%"></div></div><span class="value">{Hours(item.Seconds)} h</span></div>""").Append('\n');
        html.Append($$"""
            <footer>
            <p>{{summary.SessionCount}} 局会话，时长含挂机，有效时长 {{Hours(summary.EffectiveSeconds)}} 小时。一天从 {{metadata.DayBoundaryHour}}:00 开始。完整数据在 data 目录，说明见 README.md。</p>
            <p>{{summary.SessionCount}} sessions. Hours include idle time; {{Hours(summary.EffectiveSeconds)}} hours were active. A day starts at {{metadata.DayBoundaryHour}}:00. Full data is in the data folder, see README.md.</p>
            <p>Steam Stat {{Encode(metadata.AppVersion)}} · {{DateTimeOffset.FromUnixTimeSeconds(metadata.CreatedAt).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}}</p>
            </footer>
            </body>
            </html>

            """);
        return html.ToString();
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    private static string ToReadme(OpenArchiveScope scope, OpenArchiveMetadata metadata, OpenArchiveSummary summary)
    {
        var columns = new StringBuilder();
        foreach (var (name, description) in SemanticCatalog.ColumnDescriptions.Where(item => !item.Key.Contains('.')).OrderBy(item => item.Key, StringComparer.Ordinal))
            columns.Append($"| `{name}` | {description.Zh} | {description.En} |\n");
        return $$"""
            # Steam Stat 档案：{{scope.Label}}

            这是一份封存的游戏档案，生成后不再修改。不需要 Steam Stat 也能打开：

            - `index.html`：用浏览器打开就是这段时间的概览，不需要联网。
            - `data/*.csv`：表格数据，Excel、Numbers、LibreOffice 都能打开。
            - `data/archive.json`：同样的内容，方便写程序读取。
            - `manifest.json`：每个文件的 SHA-256，用来确认文件没有被改动。

            范围 {{scope.FromDay}} 至 {{scope.ToDay}}，时区 {{metadata.TimeZone}}，一天从 {{metadata.DayBoundaryHour}}:00 开始（「逻辑日」）。
            共 {{summary.SessionCount}} 局会话、{{Hours(summary.TotalSeconds)}} 小时，由 Steam Stat {{metadata.AppVersion}} 生成。

            时间列是 Unix 秒。时长含挂机，`effective_seconds` 是扣除挂机后的时长。
            隐藏的游戏不在其中，好友的动态也不在其中（那是别人的数据）。

            # Steam Stat archive: {{scope.Label}}

            A sealed gaming archive. It is not modified after creation and opens without Steam Stat:

            - `index.html`: open it in a browser for an overview. No network needed.
            - `data/*.csv`: tables for Excel, Numbers or LibreOffice.
            - `data/archive.json`: the same content for programs.
            - `manifest.json`: SHA-256 of every file, to confirm nothing changed.

            Covers {{scope.FromDay}} to {{scope.ToDay}}, time zone {{metadata.TimeZone}}. A day starts at {{metadata.DayBoundaryHour}}:00 (the "logical day").
            {{summary.SessionCount}} sessions, {{Hours(summary.TotalSeconds)}} hours, generated by Steam Stat {{metadata.AppVersion}}.

            Time columns are Unix seconds. Durations include idle time; `effective_seconds` excludes it.
            Hidden games are left out, and so is friend activity, because that data belongs to other people.

            ## 列说明 / Columns

            | 列 / Column | 说明 | Description |
            |---|---|---|
            {{columns}}
            """;
    }
}
