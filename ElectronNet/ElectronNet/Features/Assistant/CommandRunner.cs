using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using SteamStat.Core.Environment;

namespace ElectronNet.Features.Assistant;

internal sealed record CommandResult(string Output, int ExitCode, bool Clear = false);

/// <summary>
/// 命令行工具（<c>steamstat today</c>）和隐藏的 CRT 终端共用的命令。都在 Host 里执行：
/// 命令行经本地接口的 /v1/cli 调过来，终端经 IPC，数据都走同一套只读工具和分级授权。
/// </summary>
internal sealed class CommandRunner(ArchiveToolCatalog tools, AppEnvironment environment)
{
    internal const int ExitOk = 0;
    internal const int ExitToolError = 2;
    internal const int ExitUsage = 64;

    public async Task<CommandResult> RunAsync(IReadOnlyList<string> arguments, ToolContext context, CancellationToken cancellationToken = default)
    {
        var json = arguments.Any(item => item is "--json" or "-j");
        var args = arguments.Where(item => item is not ("--json" or "-j")).ToList();
        var command = args.Count == 0 ? "help" : args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToList();
        var zh = context.Chinese;

        switch (command)
        {
            case "help" or "-h" or "--help" or "?":
                return new CommandResult(Help(zh), ExitOk);
            case "version" or "--version" or "-v":
                return json
                    ? Json(new JsonObject { ["app"] = "steam-stat", ["version"] = environment.AppVersion })
                    : new CommandResult($"Steam Stat {environment.AppVersion}", ExitOk);
            case "clear" or "cls":
                return new CommandResult(string.Empty, ExitOk, Clear: true);
            case "tools":
                var available = tools.Available(context.Tiers);
                if (json)
                    return Json(new JsonObject
                    {
                        ["tools"] = new JsonArray(available.Select(tool => (JsonNode)new JsonObject
                        {
                            ["name"] = tool.Name, ["tier"] = tool.Tier, ["title"] = tool.Title, ["description"] = tool.Description
                        }).ToArray())
                    });
                return new CommandResult(Table(["tool", "scope", "title"], available.Select(tool => new object?[] { tool.Name, tool.Tier, tool.Title }).ToList()), ExitOk);
            case "today":
                return await TodayAsync(context, json, cancellationToken).ConfigureAwait(false);
            case "now":
                return await SingleAsync("get_now_playing", [], context, json, zh ? "正在玩" : "Now playing", cancellationToken).ConfigureAwait(false);
            case "days" or "week" or "month":
                var days = command switch { "week" => 7, "month" => 30, _ => Number(rest, 0) ?? 14 };
                return await DaysAsync(Math.Clamp(days, 1, 366), context, json, cancellationToken).ConfigureAwait(false);
            case "top":
                var top = new JsonObject { ["limit"] = Number(rest, 0) ?? 10 };
                if (Option(rest, "--days") is { } window && int.TryParse(window, CultureInfo.InvariantCulture, out var span) && span > 0)
                {
                    top["from"] = Day(tools.Today, -(Math.Min(span, 36500) - 1));
                    top["to"] = tools.Today;
                }
                return await SingleAsync("get_top_games", top, context, json, zh ? "最常玩" : "Most played", cancellationToken).ConfigureAwait(false);
            case "sessions":
                return await SingleAsync("list_sessions", new JsonObject { ["limit"] = Number(rest, 0) ?? 20 }, context, json, zh ? "最近的会话" : "Recent sessions", cancellationToken).ConfigureAwait(false);
            case "achievements":
                return await SingleAsync("get_recent_achievements", new JsonObject { ["limit"] = Number(rest, 0) ?? 15 }, context, json, zh ? "最近解锁" : "Recently unlocked", cancellationToken).ConfigureAwait(false);
            case "metrics":
                return await SingleAsync("list_metrics", [], context, json, zh ? "指标" : "Metrics", cancellationToken).ConfigureAwait(false);
            case "game":
                return await GameAsync(string.Join(' ', rest), context, json, cancellationToken).ConfigureAwait(false);
            case "sql":
                if (rest.Count == 0) return Usage(zh, "sql <SELECT ...>");
                return await SingleAsync("run_sql", new JsonObject { ["sql"] = string.Join(' ', rest) }, context, json, null, cancellationToken).ConfigureAwait(false);
            case "call":
                if (rest.Count == 0) return Usage(zh, "call <tool> [json]");
                JsonObject callArguments;
                try
                {
                    callArguments = rest.Count > 1 ? JsonNode.Parse(string.Join(' ', rest.Skip(1))) as JsonObject ?? [] : [];
                }
                catch (System.Text.Json.JsonException)
                {
                    return Usage(zh, "call <tool> {\"arg\": 1}");
                }
                return await SingleAsync(rest[0], callArguments, context, json, null, cancellationToken).ConfigureAwait(false);
            default:
                return new CommandResult(zh ? $"未知命令：{command}。输入 help 查看可用命令。" : $"Unknown command: {command}. Type help for the list.", ExitUsage);
        }
    }

    private async Task<CommandResult> TodayAsync(ToolContext context, bool json, CancellationToken cancellationToken)
    {
        var zh = context.Chinese;
        var overview = await tools.ExecuteAsync("get_overview", [], context, cancellationToken).ConfigureAwait(false);
        if (overview.Error != null) return Failure(overview.Error, json);
        var today = tools.Today;
        var seconds = overview.Rows.Count > 0 ? Convert.ToInt64(overview.Rows[0][1], CultureInfo.InvariantCulture) : 0;
        var hasGames = context.Tiers.Contains(Semantic.SemanticTier.Games);
        ToolTable? now = null, top = null;
        if (hasGames)
        {
            now = await tools.ExecuteAsync("get_now_playing", [], context, cancellationToken).ConfigureAwait(false);
            top = await tools.ExecuteAsync("get_top_games", new JsonObject { ["from"] = today, ["to"] = today, ["limit"] = 5 }, context, cancellationToken).ConfigureAwait(false);
        }

        if (json)
        {
            var result = new JsonObject { ["day"] = today, ["seconds"] = seconds, ["hours"] = Math.Round(seconds / 3600.0, 2) };
            if (now is { Error: null }) result["nowPlaying"] = now.ToObjects();
            if (top is { Error: null }) result["topGames"] = top.ToObjects();
            return Json(result);
        }

        var builder = new StringBuilder();
        builder.Append(zh ? "今天" : "Today").Append(" (").Append(today).Append(")  ").AppendLine(Duration(seconds));
        if (now is { Error: null, Rows.Count: > 0 })
            foreach (var row in now.Rows)
                builder.Append(zh ? "正在玩：" : "Now playing: ").Append(row[1]).Append(" (").Append(Duration((long)(Convert.ToDouble(row[3], CultureInfo.InvariantCulture) * 60))).AppendLine(")");
        if (top is { Error: null, Rows.Count: > 0 })
        {
            builder.AppendLine();
            var max = top.Rows.Max(row => Convert.ToDouble(row[3], CultureInfo.InvariantCulture));
            var width = top.Rows.Max(row => DisplayWidth(Convert.ToString(row[1], CultureInfo.InvariantCulture) ?? ""));
            foreach (var row in top.Rows)
            {
                var name = Convert.ToString(row[1], CultureInfo.InvariantCulture) ?? "";
                var value = Convert.ToDouble(row[3], CultureInfo.InvariantCulture);
                builder.Append("  ").Append(Pad(name, width)).Append("  ").Append(Duration((long)value).PadLeft(7)).Append("  ").AppendLine(Bar(value, max, 24));
            }
        }
        else if (!hasGames)
        {
            builder.AppendLine(zh ? "（没有授权游戏名，只显示总时长）" : "(Game names are not authorized; showing the total only.)");
        }
        return new CommandResult(builder.ToString().TrimEnd(), ExitOk);
    }

    private async Task<CommandResult> DaysAsync(int days, ToolContext context, bool json, CancellationToken cancellationToken)
    {
        var table = await tools.ExecuteAsync("get_daily_playtime", new JsonObject { ["from"] = Day(tools.Today, -(days - 1)), ["to"] = tools.Today }, context, cancellationToken).ConfigureAwait(false);
        if (table.Error != null) return Failure(table.Error, json);
        if (json) return Json(new JsonObject { ["days"] = table.ToObjects() });
        var byDay = table.Rows.ToDictionary(row => Convert.ToString(row[0], CultureInfo.InvariantCulture)!, row => Convert.ToDouble(row[2], CultureInfo.InvariantCulture));
        var max = byDay.Count == 0 ? 1 : byDay.Values.Max();
        var builder = new StringBuilder();
        for (var offset = days - 1; offset >= 0; offset--)
        {
            var day = Day(tools.Today, -offset);
            var value = byDay.GetValueOrDefault(day);
            builder.Append(day).Append("  ").Append(Duration((long)value).PadLeft(7)).Append("  ").AppendLine(Bar(value, max, 30));
        }
        builder.Append(context.Chinese ? "合计 " : "Total ").Append(Duration((long)byDay.Values.Sum()));
        return new CommandResult(builder.ToString(), ExitOk);
    }

    private async Task<CommandResult> GameAsync(string query, ToolContext context, bool json, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query)) return Usage(context.Chinese, "game <name | app id>");
        if (!int.TryParse(query, CultureInfo.InvariantCulture, out var appId))
        {
            var found = await tools.ExecuteAsync("find_games", new JsonObject { ["query"] = query }, context, cancellationToken).ConfigureAwait(false);
            if (found.Error != null) return Failure(found.Error, json);
            if (found.Rows.Count != 1)
                return found.Rows.Count == 0
                    ? new CommandResult(context.Chinese ? $"没有找到「{query}」" : $"No game matches \"{query}\"", ExitToolError)
                    : Render(found, json, context.Chinese ? "找到多款，用 app id 再查一次" : "Several matches; run again with an app id");
            appId = Convert.ToInt32(found.Rows[0][0], CultureInfo.InvariantCulture);
        }
        var table = await tools.ExecuteAsync("get_game", new JsonObject { ["app_id"] = appId }, context, cancellationToken).ConfigureAwait(false);
        if (table.Error != null || json || table.Rows.Count == 0) return Render(table, json, null);
        // 一款游戏只有一行：竖着排，键值对更好读
        var width = table.Columns.Max(column => column.Length);
        var builder = new StringBuilder();
        for (var index = 0; index < table.Columns.Count; index++)
            builder.Append(table.Columns[index].PadRight(width)).Append("  ").AppendLine(Convert.ToString(table.Rows[0][index], CultureInfo.InvariantCulture) ?? "—");
        return new CommandResult(builder.ToString().TrimEnd(), ExitOk);
    }

    private async Task<CommandResult> SingleAsync(string tool, JsonObject arguments, ToolContext context, bool json, string? title, CancellationToken cancellationToken)
        => Render(await tools.ExecuteAsync(tool, arguments, context, cancellationToken).ConfigureAwait(false), json, title);

    private static CommandResult Render(ToolTable table, bool json, string? title)
    {
        if (table.Error != null) return Failure(table.Error, json);
        if (json)
        {
            var result = new JsonObject { ["columns"] = new JsonArray(table.Columns.Select(column => (JsonNode)column).ToArray()), ["items"] = table.ToObjects() };
            if (table.Truncated) result["truncated"] = true;
            if (table.Note != null) result["note"] = table.Note;
            return Json(result);
        }
        var builder = new StringBuilder();
        if (title != null) builder.AppendLine(title);
        builder.Append(table.Rows.Count == 0 ? table.Note ?? "—" : Table(table.Columns, table.Rows));
        if (table.Truncated) builder.AppendLine().Append("…");
        return new CommandResult(builder.ToString().TrimEnd(), ExitOk);
    }

    private static CommandResult Failure(string error, bool json)
        => json ? new CommandResult(new JsonObject { ["error"] = error }.ToJsonString(), ExitToolError) : new CommandResult(error, ExitToolError);

    private static CommandResult Json(JsonObject value) => new(value.ToJsonString(), ExitOk);

    private static CommandResult Usage(bool zh, string usage) => new((zh ? "用法：" : "Usage: ") + usage, ExitUsage);

    private static string Help(bool zh) => zh
        ? """
          Steam Stat 命令（加 --json 输出 JSON）
            today                今天玩了多久、正在玩什么
            now                  正在玩
            days [N] | week | month   每天的时长（默认 14 天）
            top [N] [--days D]   最常玩的游戏
            game <名字|app id>   一款游戏的档案
            sessions [N]         最近的会话
            achievements [N]     最近解锁的成就
            metrics              指标字典
            sql <SELECT ...>     在语义视图上跑只读 SQL
            tools                可用的工具（受授权级别限制）
            call <工具> [json]   直接调用一个工具
            version              版本
          """
        : """
          Steam Stat commands (add --json for JSON output)
            today                today's playtime and what is running
            now                  games running right now
            days [N] | week | month   playtime per day (default 14 days)
            top [N] [--days D]   most played games
            game <name|app id>   everything about one game
            sessions [N]         recent sessions
            achievements [N]     recently unlocked achievements
            metrics              the metric dictionary
            sql <SELECT ...>     read-only SQL on the semantic views
            tools                available tools (limited by the granted scopes)
            call <tool> [json]   call a tool directly
            version              version
          """;

    private static int? Number(IReadOnlyList<string> args, int index)
        => args.Count > index && int.TryParse(args[index], CultureInfo.InvariantCulture, out var value) ? value : null;

    private static string? Option(IReadOnlyList<string> args, string name)
    {
        var index = args.ToList().IndexOf(name);
        return index >= 0 && index + 1 < args.Count ? args[index + 1] : null;
    }

    private static string Day(string today, int offset)
        => DateOnly.ParseExact(today, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static string Duration(long seconds)
    {
        var minutes = Math.Max(0, seconds) / 60;
        return minutes >= 60 ? $"{minutes / 60}h {minutes % 60:00}m" : $"{minutes}m";
    }

    private static string Bar(double value, double max, int width)
    {
        if (max <= 0 || value <= 0) return string.Empty;
        var cells = value / max * width;
        var full = (int)cells;
        return new string('█', full) + (cells - full >= 0.5 ? "▌" : string.Empty);
    }

    internal static string Table(IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows)
    {
        var cells = rows.Select(row => row.Select(Cell).ToArray()).ToList();
        var widths = columns.Select((column, index) => Math.Min(48, Math.Max(DisplayWidth(column), cells.Count == 0 ? 0 : cells.Max(row => index < row.Length ? DisplayWidth(row[index]) : 0)))).ToArray();
        var builder = new StringBuilder();
        builder.AppendLine(string.Join("  ", columns.Select((column, index) => Pad(column, widths[index]))).TrimEnd());
        builder.AppendLine(string.Join("  ", widths.Select(width => new string('─', width))));
        foreach (var row in cells)
            builder.AppendLine(string.Join("  ", widths.Select((width, index) => Pad(index < row.Length ? row[index] : "", width))).TrimEnd());
        return builder.ToString().TrimEnd();
    }

    private static string Cell(object? value) => value switch
    {
        null => "—",
        double number => number.ToString("0.##", CultureInfo.InvariantCulture),
        _ => (Convert.ToString(value, CultureInfo.InvariantCulture) ?? "").ReplaceLineEndings(" ")
    };

    /// <summary>中日韩文字和全角符号占两格，否则表格对不齐。</summary>
    internal static int DisplayWidth(string text) => text.Sum(character => IsWide(character) ? 2 : 1);

    private static bool IsWide(char character)
        => character is >= '\u1100' and <= '\u115F' or >= '\u2E80' and <= '\uA4CF' or >= '\uAC00' and <= '\uD7A3' or >= '\uF900' and <= '\uFAFF' or >= '\uFE30' and <= '\uFE4F' or >= '\uFF00' and <= '\uFF60' or >= '\uFFE0' and <= '\uFFE6';

    private static string Pad(string text, int width)
    {
        var current = DisplayWidth(text);
        if (current > width)
        {
            var builder = new StringBuilder();
            var used = 0;
            foreach (var character in text)
            {
                var size = IsWide(character) ? 2 : 1;
                if (used + size > width - 1) break;
                builder.Append(character);
                used += size;
            }
            return builder.Append('…').Append(new string(' ', Math.Max(0, width - used - 1))).ToString();
        }
        return text + new string(' ', width - current);
    }
}
