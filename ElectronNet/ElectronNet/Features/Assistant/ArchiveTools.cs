using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ElectronNet.Features.Semantic;
using ElectronNet.Services;
using SteamStat.Core.Assistant;
using SteamStat.Core.Settings;

namespace ElectronNet.Features.Assistant;

/// <summary>
/// 一次外部调用的边界：允许读哪些分级、要不要把 SteamID 换成代号、最多回多少行、覆盖哪些账号。
/// AI、MCP、本地接口、命令行和隐藏终端各自构造一个，工具本身不知道调用方是谁。
/// </summary>
internal sealed class ToolContext(IReadOnlySet<string> tiers, bool maskIds, int maxRows, IReadOnlyList<string> steamIds, bool chinese)
{
    public IReadOnlySet<string> Tiers { get; } = tiers;
    public bool MaskIds { get; } = maskIds;
    public int MaxRows { get; } = maxRows;
    /// <summary>空表示全部账号。</summary>
    public IReadOnlyList<string> SteamIds { get; } = steamIds;
    public bool Chinese { get; } = chinese;
    public IdMasker Ids { get; } = new();
}

/// <summary>同一次对话里同一个 SteamID 总是同一个代号，模型才能把几次查询的结果对上。</summary>
internal sealed partial class IdMasker
{
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);

    public string Alias(string steamId)
    {
        if (!_aliases.TryGetValue(steamId, out var alias)) _aliases[steamId] = alias = $"player-{_aliases.Count + 1}";
        return alias;
    }

    public object? Mask(object? value) => value switch
    {
        long number when SteamId().IsMatch(number.ToString(CultureInfo.InvariantCulture)) => Alias(number.ToString(CultureInfo.InvariantCulture)),
        string text => SteamId().Replace(text, match => Alias(match.Value)),
        _ => value
    };

    [GeneratedRegex(@"7656119\d{10}")]
    private static partial Regex SteamId();
}

internal sealed record ToolChart(string Kind, string X, IReadOnlyList<string> Y);

internal sealed record ToolTable(
    IReadOnlyList<string> Columns,
    IReadOnlyList<object?[]> Rows,
    bool Truncated = false,
    string? Error = null,
    ToolChart? Chart = null,
    string? Note = null)
{
    public static ToolTable Fail(string error) => new([], [], Error: error);

    /// <summary>给模型和本地调用方的紧凑 JSON。</summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject();
        if (Error != null)
        {
            json["error"] = Error;
            return json;
        }
        json["columns"] = new JsonArray(Columns.Select(column => (JsonNode)column).ToArray());
        json["rows"] = JsonSerializer.SerializeToNode(Rows);
        if (Truncated) json["truncated"] = true;
        if (Note != null) json["note"] = Note;
        return json;
    }

    /// <summary>行转成对象，方便 REST 调用方按列名取值。</summary>
    public JsonArray ToObjects()
    {
        var array = new JsonArray();
        foreach (var row in Rows)
        {
            var item = new JsonObject();
            for (var index = 0; index < Columns.Count && index < row.Length; index++)
                item[Columns[index]] = JsonSerializer.SerializeToNode(row[index]);
            array.Add(item);
        }
        return array;
    }
}

internal sealed record ArchiveTool(
    string Name,
    string Tier,
    string Title,
    string Description,
    JsonObject Schema,
    Func<JsonObject, ToolContext, CancellationToken, Task<ToolTable>> Run);

/// <summary>
/// 档案的只读工具。全部经语义视图网关执行，所以隐藏游戏、口径设置和分级授权自动生效；
/// 一个工具需要的分级没有授权时直接拒绝，不靠模型自觉。AI 的「问档案」、MCP、本地接口、命令行和隐藏终端都用这一份。
/// </summary>
internal sealed class ArchiveToolCatalog
{
    private const string DayPattern = "^\\d{4}-\\d{2}-\\d{2}$";

    private readonly SemanticLayerService _semantic;
    private readonly UseAppRecordService _records;
    private readonly ISettingsStore _settings;
    private readonly TimeProvider _time;

    public IReadOnlyList<ArchiveTool> All { get; }

    public ArchiveToolCatalog(SemanticLayerService semantic, UseAppRecordService records, ISettingsStore settings, TimeProvider time)
    {
        _semantic = semantic;
        _records = records;
        _settings = settings;
        _time = time;
        All =
        [
            new("get_overview", SemanticTier.Aggregate, "Playtime overview",
                "Total playtime for today, the last 7 days, this month, this year and all time, with active days. Today includes sessions that are still running. Durations are in seconds and hours. Days are logical days (the user's day boundary), in the user's local time zone.",
                Schema(), OverviewAsync),
            new("get_daily_playtime", SemanticTier.Aggregate, "Daily playtime",
                "Playtime per logical day in a date range (default: the last 30 days, at most 731 days). Days without play are omitted.",
                Schema(("from", Day("First day, YYYY-MM-DD")), ("to", Day("Last day, YYYY-MM-DD"))), DailyAsync),
            new("get_top_games", SemanticTier.Games, "Most played games",
                "Games ranked by playtime, optionally within a date range (default: all time). Returns app_id, name, hours, seconds and session count.",
                Schema(("from", Day("First day, YYYY-MM-DD")), ("to", Day("Last day, YYYY-MM-DD")), ("limit", Int("Number of games (1-50), default 10"))), TopGamesAsync),
            new("find_games", SemanticTier.Games, "Find games by name",
                "Search the user's games by (part of) the name to get the app_id needed by other tools.",
                Schema(["query"], ("query", Text("Part of the game name"))), FindGamesAsync),
            new("get_game", SemanticTier.Games, "Game details",
                "Everything recorded about one game: developer, playtime, sessions, first and last played, longest session, play state (active / paused / abandoned or the user's manual status), achievements and Steam's official playtime.",
                Schema(["app_id"], ("app_id", Int("Steam app id"))), GameAsync),
            new("list_sessions", SemanticTier.Games, "Play sessions",
                "Recorded play sessions, newest first, optionally for one game and a date range (default: the last 30 days). Tags: late_night, coop, idle, comeback, crash, party. The note column is only present when the user authorized notes.",
                Schema(("from", Day("First day, YYYY-MM-DD")), ("to", Day("Last day, YYYY-MM-DD")), ("app_id", Int("Steam app id")), ("limit", Int("Number of sessions (1-200), default 20"))), SessionsAsync),
            new("get_recent_achievements", SemanticTier.Games, "Recent achievements",
                "Achievements unlocked by the user, newest first, optionally for one game. Includes the global unlock percentage when known.",
                Schema(("app_id", Int("Steam app id")), ("limit", Int("Number of achievements (1-100), default 20"))), RecentAchievementsAsync),
            new("get_achievement_progress", SemanticTier.Games, "Achievement progress",
                "Achievement completion per game (Steam's rules: base set plus owned DLC sets; all_* counts include everything). Sorted by completion.",
                Schema(("app_id", Int("Steam app id")), ("limit", Int("Number of games (1-100), default 20"))), AchievementProgressAsync),
            new("get_now_playing", SemanticTier.Games, "Now playing",
                "Games that are running right now and for how long.",
                Schema(), NowPlayingAsync),
            new("get_friends_together", SemanticTier.Friends, "Time with friends",
                "Friends ranked by time spent in the same game session as the user (confirmed lobbies, servers or Rich Presence groups), optionally within a date range.",
                Schema(("from", Day("First day, YYYY-MM-DD")), ("to", Day("Last day, YYYY-MM-DD")), ("limit", Int("Number of friends (1-50), default 10"))), FriendsTogetherAsync),
            new("list_metrics", SemanticTier.Aggregate, "Metric dictionary",
                "The app's defined metrics with their current values. Metrics that need a scope the user has not granted are listed without a value.",
                Schema(), MetricsAsync),
            new("describe_views", SemanticTier.Aggregate, "Semantic views",
                "The read-only SQL views that run_sql can query, with their columns. Only views the user authorized are listed.",
                Schema(), DescribeViewsAsync),
            new("run_sql", SemanticTier.Aggregate, "Read-only SQL",
                "Run one read-only SQLite SELECT against the semantic views (call describe_views first). Only v_* views are visible; anything else is denied. Times are Unix seconds; use date(x, 'unixepoch', 'localtime'). Results are not limited to the user's own accounts automatically.",
                Schema(["sql"], ("sql", Text("A single SELECT statement"))), SqlAsync)
        ];
    }

    public ArchiveTool? Find(string name) => All.FirstOrDefault(tool => tool.Name == name);

    /// <summary>这次调用能用的工具：需要的分级都授权了才列出。</summary>
    public IReadOnlyList<ArchiveTool> Available(IReadOnlySet<string> tiers) => All.Where(tool => tiers.Contains(tool.Tier)).ToList();

    public static ToolSpec Spec(ArchiveTool tool) => new(tool.Name, tool.Title, tool.Description, tool.Schema);

    public async Task<ToolTable> ExecuteAsync(string name, JsonObject arguments, ToolContext context, CancellationToken cancellationToken = default)
    {
        var tool = Find(name);
        if (tool == null) return ToolTable.Fail($"unknown_tool: {name}");
        if (!context.Tiers.Contains(tool.Tier))
            return ToolTable.Fail($"not_authorized: this tool needs the '{tool.Tier}' scope, which the user has not granted in Steam Stat.");
        ToolTable table;
        try
        {
            table = await tool.Run(arguments, context, cancellationToken).ConfigureAwait(false);
        }
        catch (ToolArgumentException exception)
        {
            return ToolTable.Fail($"invalid_arguments: {exception.Message}");
        }
        return context.MaskIds ? Mask(table, context.Ids) : table;
    }

    private static ToolTable Mask(ToolTable table, IdMasker ids)
        => table with { Rows = table.Rows.Select(row => row.Select(ids.Mask).ToArray()).ToList() };

    // ------------------------------------------------------------------
    // 工具实现
    // ------------------------------------------------------------------

    private async Task<ToolTable> OverviewAsync(JsonObject arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        var today = LogicalDay(now);
        var date = DateOnly.ParseExact(today, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var periods = new (string Name, string From)[]
        {
            ("today", today),
            ("last_7_days", Format(date.AddDays(-6))),
            ("this_month", Format(new DateOnly(date.Year, date.Month, 1))),
            ("this_year", Format(new DateOnly(date.Year, 1, 1))),
            ("all_time", "0000-01-01")
        };
        var (scope, parameters) = Scope(context, "steam_id");
        var sql = string.Join(" UNION ALL ", periods.Select((period, index) =>
        {
            parameters[$"$p{index}"] = period.From;
            parameters[$"$n{index}"] = period.Name;
            return $"SELECT $n{index} AS period, COALESCE(SUM(seconds), 0) AS seconds, COUNT(DISTINCT logical_day) AS active_days FROM v_daily_playtime WHERE logical_day >= $p{index}{scope}";
        }));
        var result = await QueryAsync(sql, context, parameters, cancellationToken).ConfigureAwait(false);
        if (result.Error != null) return result;

        // 进行中的会话不在视图里（还没结束），今天的数字要把它们加上
        var running = OngoingSeconds(context, now, today);
        var rows = result.Rows.Select(row =>
        {
            var seconds = Convert.ToDouble(row[1], CultureInfo.InvariantCulture) + running;
            return new object?[] { row[0], (long)seconds, Math.Round(seconds / 3600, 2), row[2] };
        }).ToList();
        return new ToolTable(["period", "seconds", "hours", "active_days"], rows,
            Note: running > 0 ? $"Includes {running} seconds from sessions that are still running. Today is {today}." : $"Today is {today}.");
    }

    private Task<ToolTable> DailyAsync(JsonObject arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var (from, to) = Range(arguments, 30, 731);
        var (scope, parameters) = Scope(context, "steam_id");
        parameters["$from"] = from;
        parameters["$to"] = to;
        return QueryAsync($"""
            SELECT logical_day, ROUND(SUM(seconds) / 3600.0, 2) AS hours, CAST(SUM(seconds) AS INTEGER) AS seconds,
                   CAST(SUM(effective_seconds) AS INTEGER) AS effective_seconds, SUM(session_count) AS sessions
            FROM v_daily_playtime
            WHERE logical_day BETWEEN $from AND $to{scope}
            GROUP BY logical_day ORDER BY logical_day
            """, context, parameters, cancellationToken, new ToolChart("line", "logical_day", ["hours"]), 800);
    }

    private Task<ToolTable> TopGamesAsync(JsonObject arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var limit = Limit(arguments, 10, 50);
        var chart = new ToolChart("bar", "name", ["hours"]);
        if (OptionalDay(arguments, "from") == null && OptionalDay(arguments, "to") == null)
        {
            var (scope, parameters) = Scope(context, "steam_id");
            parameters["$limit"] = limit;
            return QueryAsync($"""
                SELECT app_id, MIN(app_name) AS name, ROUND(SUM(seconds) / 3600.0, 2) AS hours, CAST(SUM(seconds) AS INTEGER) AS seconds,
                       SUM(session_count) AS sessions, date(MAX(last_played), 'unixepoch', 'localtime') AS last_played
                FROM v_app_playtime WHERE 1 = 1{scope}
                GROUP BY app_id ORDER BY SUM(seconds) DESC LIMIT $limit
                """, context, parameters, cancellationToken, chart);
        }
        var (from, to) = Range(arguments, 30, 36500);
        var (rangeScope, rangeParameters) = Scope(context, "d.steam_id");
        rangeParameters["$from"] = from;
        rangeParameters["$to"] = to;
        rangeParameters["$limit"] = limit;
        return QueryAsync($"""
            SELECT d.app_id, (SELECT a.name FROM v_app a WHERE a.app_id = d.app_id) AS name,
                   ROUND(SUM(d.seconds) / 3600.0, 2) AS hours, CAST(SUM(d.seconds) AS INTEGER) AS seconds,
                   COUNT(DISTINCT d.session_id) AS sessions, MAX(d.logical_day) AS last_day
            FROM v_session_day d
            WHERE d.logical_day BETWEEN $from AND $to{rangeScope}
            GROUP BY d.app_id ORDER BY SUM(d.seconds) DESC LIMIT $limit
            """, context, rangeParameters, cancellationToken, chart);
    }

    private Task<ToolTable> FindGamesAsync(JsonObject arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var query = RequiredText(arguments, "query", 100);
        var (scope, parameters) = Scope(context, "p.steam_id");
        parameters["$query"] = "%" + query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        return QueryAsync($"""
            SELECT a.app_id, a.name,
                   (SELECT ROUND(SUM(p.seconds) / 3600.0, 2) FROM v_app_playtime p WHERE p.app_id = a.app_id{scope}) AS hours
            FROM v_app a
            WHERE a.name LIKE $query ESCAPE '\'
            ORDER BY hours IS NULL, hours DESC LIMIT 20
            """, context, parameters, cancellationToken);
    }

    private Task<ToolTable> GameAsync(JsonObject arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var appId = RequiredInt(arguments, "app_id");
        var (scope, parameters) = Scope(context, "steam_id");
        parameters["$app"] = appId;
        return QueryAsync($"""
            SELECT a.app_id, a.name, a.developer, a.publisher, a.release_date,
                   (SELECT ROUND(SUM(seconds) / 3600.0, 2) FROM v_app_playtime WHERE app_id = $app{scope}) AS hours,
                   (SELECT SUM(session_count) FROM v_app_playtime WHERE app_id = $app{scope}) AS sessions,
                   (SELECT datetime(MIN(first_played), 'unixepoch', 'localtime') FROM v_app_playtime WHERE app_id = $app{scope}) AS first_played,
                   (SELECT datetime(MAX(last_played), 'unixepoch', 'localtime') FROM v_app_playtime WHERE app_id = $app{scope}) AS last_played,
                   (SELECT ROUND(MAX(longest_session_seconds) / 60.0, 1) FROM v_app_playtime WHERE app_id = $app{scope}) AS longest_session_minutes,
                   (SELECT state FROM v_game_state WHERE app_id = $app{scope} ORDER BY last_played DESC LIMIT 1) AS state,
                   (SELECT MAX(unlocked) FROM v_achievement_game WHERE app_id = $app{scope}) AS achievements_unlocked,
                   (SELECT MAX(total) FROM v_achievement_game WHERE app_id = $app{scope}) AS achievements_total,
                   (SELECT ROUND(MAX(playtime_minutes) / 60.0, 1) FROM v_official_playtime WHERE app_id = $app{scope}) AS steam_official_hours
            FROM v_app a WHERE a.app_id = $app
            """, context, parameters, cancellationToken);
    }

    private async Task<ToolTable> SessionsAsync(JsonObject arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var (from, to) = Range(arguments, 30, 36500);
        var limit = Limit(arguments, 20, 200);
        var appId = OptionalInt(arguments, "app_id");
        var (scope, parameters) = Scope(context, "s.steam_id");
        parameters["$from"] = from;
        parameters["$to"] = to;
        parameters["$limit"] = limit;
        var appFilter = string.Empty;
        if (appId != null)
        {
            appFilter = " AND s.app_id = $app";
            parameters["$app"] = appId.Value;
        }
        var notes = context.Tiers.Contains(SemanticTier.Notes)
            ? ", (SELECT n.note FROM v_session_note n WHERE n.record_id = s.session_id) AS note"
            : string.Empty;
        var table = await QueryAsync($"""
            SELECT s.app_id, s.app_name AS name, datetime(s.start_time, 'unixepoch', 'localtime') AS started,
                   ROUND(s.duration_seconds / 60.0, 1) AS minutes, ROUND(s.effective_seconds / 60.0, 1) AS effective_minutes,
                   s.logical_day, s.auto_tags{notes}
            FROM v_session s
            WHERE s.logical_day BETWEEN $from AND $to{appFilter}{scope}
            ORDER BY s.start_time DESC LIMIT $limit
            """, context, parameters, cancellationToken).ConfigureAwait(false);
        if (table.Error != null) return table;
        var tagIndex = table.Columns.ToList().IndexOf("auto_tags");
        var columns = table.Columns.ToArray();
        columns[tagIndex] = "tags";
        return table with
        {
            Columns = columns,
            Rows = table.Rows.Select(row =>
            {
                var copy = row.ToArray();
                copy[tagIndex] = TagNames(row[tagIndex] == null ? 0 : Convert.ToInt32(row[tagIndex], CultureInfo.InvariantCulture));
                return copy;
            }).ToList()
        };
    }

    private Task<ToolTable> RecentAchievementsAsync(JsonObject arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var limit = Limit(arguments, 20, 100);
        var (scope, parameters) = Scope(context, "u.steam_id");
        parameters["$limit"] = limit;
        var appFilter = string.Empty;
        if (OptionalInt(arguments, "app_id") is { } appId)
        {
            appFilter = " AND u.app_id = $app";
            parameters["$app"] = appId;
        }
        return QueryAsync($"""
            SELECT u.app_id, (SELECT a.name FROM v_app a WHERE a.app_id = u.app_id) AS game, u.name AS achievement, u.description,
                   ROUND(u.global_percent, 2) AS global_percent, datetime(u.unlock_time, 'unixepoch', 'localtime') AS unlocked_at
            FROM v_achievement_unlock u
            WHERE 1 = 1{appFilter}{scope}
            ORDER BY u.unlock_time DESC LIMIT $limit
            """, context, parameters, cancellationToken);
    }

    private Task<ToolTable> AchievementProgressAsync(JsonObject arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var limit = Limit(arguments, 20, 100);
        var (scope, parameters) = Scope(context, "g.steam_id");
        parameters["$limit"] = limit;
        var appFilter = string.Empty;
        if (OptionalInt(arguments, "app_id") is { } appId)
        {
            appFilter = " AND g.app_id = $app";
            parameters["$app"] = appId;
        }
        return QueryAsync($"""
            SELECT g.app_id, g.app_name AS name, g.unlocked, g.total, ROUND(100.0 * g.unlocked / g.total, 1) AS percent,
                   g.all_unlocked, g.all_total, g.steam_id
            FROM v_achievement_game g
            WHERE g.total > 0{appFilter}{scope}
            ORDER BY percent DESC, g.total DESC LIMIT $limit
            """, context, parameters, cancellationToken);
    }

    private Task<ToolTable> NowPlayingAsync(JsonObject arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        var rows = OngoingSessions(context)
            .Select(session => new object?[]
            {
                session.AppId,
                session.AppName,
                DateTimeOffset.FromUnixTimeSeconds(session.StartTime).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Math.Round(Math.Max(0, now - session.StartTime) / 60.0, 1),
                session.SteamId
            })
            .ToList();
        return Task.FromResult(new ToolTable(["app_id", "name", "started", "minutes", "steam_id"], rows,
            Note: rows.Count == 0 ? "Nothing is running right now." : null));
    }

    private Task<ToolTable> FriendsTogetherAsync(JsonObject arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var (from, to) = Range(arguments, 36500, 36500);
        var limit = Limit(arguments, 10, 50);
        var (scope, parameters) = Scope(context, "t.steam_id");
        parameters["$from"] = from;
        parameters["$to"] = to;
        parameters["$limit"] = limit;
        return QueryAsync($"""
            SELECT t.friend_steam_id,
                   (SELECT e.friend_persona_name FROM v_friend_event e
                    WHERE e.friend_steam_id = t.friend_steam_id AND e.friend_persona_name IS NOT NULL
                    ORDER BY e.observed_at DESC LIMIT 1) AS friend_name,
                   ROUND(SUM(t.seconds) / 3600.0, 2) AS hours, COUNT(DISTINCT t.session_id) AS sessions,
                   COUNT(DISTINCT t.app_id) AS games, MAX(t.logical_day) AS last_day
            FROM v_together t
            WHERE t.logical_day BETWEEN $from AND $to{scope}
            GROUP BY t.friend_steam_id ORDER BY SUM(t.seconds) DESC LIMIT $limit
            """, context, parameters, cancellationToken, new ToolChart("bar", "friend_name", ["hours"]));
    }

    private async Task<ToolTable> MetricsAsync(JsonObject arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var rows = new List<object?[]>();
        foreach (var metric in MetricCatalog.Metrics)
        {
            var result = await _semantic.QueryAsync(metric.Sql, context.Tiers, cancellationToken: cancellationToken).ConfigureAwait(false);
            object? value = result.Failure == SemanticQueryFailure.None && result.Rows is [{ Length: > 0 } row, ..] ? row[0] ?? 0 : null;
            rows.Add([
                metric.Id,
                context.Chinese ? metric.Name.Zh : metric.Name.En,
                value,
                metric.Unit,
                result.Failure switch
                {
                    SemanticQueryFailure.None => "ok",
                    SemanticQueryFailure.Denied => "not_authorized",
                    _ => "failed"
                },
                context.Chinese ? metric.Definition.Zh : metric.Definition.En
            ]);
        }
        return new ToolTable(["id", "name", "value", "unit", "status", "definition"], rows);
    }

    private async Task<ToolTable> DescribeViewsAsync(JsonObject arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var views = await _semantic.GetViewsAsync(cancellationToken).ConfigureAwait(false);
        var rows = views
            .Where(view => context.Tiers.Contains(view.Tier))
            .Select(view => new object?[]
            {
                view.Name,
                view.Tier,
                context.Chinese ? view.Description.Zh : view.Description.En,
                string.Join(", ", view.Columns.Select(column => $"{column.Name} ({(context.Chinese ? column.Description.Zh : column.Description.En)})"))
            })
            .ToList();
        return new ToolTable(["view", "tier", "description", "columns"], rows);
    }

    private Task<ToolTable> SqlAsync(JsonObject arguments, ToolContext context, CancellationToken cancellationToken)
        => QueryAsync(RequiredText(arguments, "sql", 4000), context, null, cancellationToken);

    // ------------------------------------------------------------------
    // 公用
    // ------------------------------------------------------------------

    private async Task<ToolTable> QueryAsync(
        string sql,
        ToolContext context,
        Dictionary<string, object?>? parameters,
        CancellationToken cancellationToken,
        ToolChart? chart = null,
        int? maxRows = null)
    {
        var limit = Math.Clamp(maxRows ?? context.MaxRows, 1, SemanticQueryGateway.DefaultMaxRows);
        var result = await _semantic.QueryAsync(sql, context.Tiers, parameters, limit, cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.Failure switch
        {
            SemanticQueryFailure.None => new ToolTable(result.Columns, result.Rows, result.Truncated, Chart: chart),
            SemanticQueryFailure.Denied => ToolTable.Fail("denied: the statement reads something outside the authorized v_* views, or is not a read-only SELECT."),
            SemanticQueryFailure.Timeout => ToolTable.Fail("timeout: the query took longer than 10 seconds."),
            _ => ToolTable.Fail("invalid: SQLite rejected the statement (syntax error or unknown column).")
        };
    }

    private (string Sql, Dictionary<string, object?> Parameters) Scope(ToolContext context, string column)
    {
        var parameters = new Dictionary<string, object?>();
        if (context.SteamIds.Count == 0) return (string.Empty, parameters);
        var names = context.SteamIds.Select((steamId, index) =>
        {
            parameters[$"$sid{index}"] = steamId;
            return $"$sid{index}";
        });
        return ($" AND {column} IN ({string.Join(", ", names)})", parameters);
    }

    /// <summary>进行中的会话（隐藏游戏已剔除，按账号范围过滤）。</summary>
    internal IEnumerable<SteamStat.Contracts.Ipc.CurrentSessionDto> OngoingSessions(ToolContext context)
    {
        var hidden = (_settings.GetSettings().Analytics?.HiddenAppIds ?? []).ToHashSet();
        return _records.GetOngoing()
            .Where(session => !hidden.Contains(session.AppId))
            .Where(session => context.SteamIds.Count == 0 || context.SteamIds.Contains(session.SteamId));
    }

    private long OngoingSeconds(ToolContext context, long now, string today)
    {
        var dayStart = DayStart(today);
        return OngoingSessions(context).Sum(session => Math.Max(0, now - Math.Max(session.StartTime, dayStart)));
    }

    private int Boundary => Math.Clamp(_settings.GetSettings().Analytics?.DayBoundaryHour ?? 4, 0, 6);

    /// <summary>和 v_session_day 同一口径：分界点之前算前一天，按本机时区。</summary>
    internal string LogicalDay(long unixSeconds)
        => DateTimeOffset.FromUnixTimeSeconds(unixSeconds - Boundary * 3600L).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal long DayStart(string day)
    {
        var date = DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).AddHours(Boundary);
        return new DateTimeOffset(local).ToUnixTimeSeconds();
    }

    internal string Today => LogicalDay(_time.GetUtcNow().ToUnixTimeSeconds());

    private (string From, string To) Range(JsonObject arguments, int defaultDays, int maxDays)
    {
        var today = DateOnly.ParseExact(Today, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var to = OptionalDay(arguments, "to") ?? today;
        var from = OptionalDay(arguments, "from") ?? to.AddDays(-(Math.Min(defaultDays, maxDays) - 1));
        if (from > to) (from, to) = (to, from);
        if (to.DayNumber - from.DayNumber >= maxDays) from = to.AddDays(-(maxDays - 1));
        return (Format(from), Format(to));
    }

    private static string Format(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string TagNames(int tags)
    {
        var names = new List<string>();
        if ((tags & 1) != 0) names.Add("late_night");
        if ((tags & 2) != 0) names.Add("coop");
        if ((tags & 4) != 0) names.Add("idle");
        if ((tags & 8) != 0) names.Add("comeback");
        if ((tags & 16) != 0) names.Add("crash");
        if ((tags & 32) != 0) names.Add("party");
        return string.Join(",", names);
    }

    // ------------------------------------------------------------------
    // 参数
    // ------------------------------------------------------------------

    private static DateOnly? OptionalDay(JsonObject arguments, string name)
    {
        var text = StringArgument(arguments, name);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : throw new ToolArgumentException($"{name} must be a date in YYYY-MM-DD format.");
    }

    private static int? OptionalInt(JsonObject arguments, string name)
    {
        var node = arguments[name];
        if (node == null) return null;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var number)) return number;
            if (value.TryGetValue<double>(out var real) && real == Math.Floor(real) && real is >= int.MinValue and <= int.MaxValue) return (int)real;
            if (value.TryGetValue<string>(out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number;
        }
        throw new ToolArgumentException($"{name} must be an integer.");
    }

    private static int RequiredInt(JsonObject arguments, string name)
        => OptionalInt(arguments, name) ?? throw new ToolArgumentException($"{name} is required.");

    private static int Limit(JsonObject arguments, int fallback, int maximum)
        => Math.Clamp(OptionalInt(arguments, "limit") ?? fallback, 1, maximum);

    private static string? StringArgument(JsonObject arguments, string name)
        => arguments[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : arguments[name]?.ToString();

    private static string RequiredText(JsonObject arguments, string name, int maxLength)
    {
        var text = StringArgument(arguments, name)?.Trim();
        if (string.IsNullOrEmpty(text)) throw new ToolArgumentException($"{name} is required.");
        if (text.Length > maxLength) throw new ToolArgumentException($"{name} is longer than {maxLength} characters.");
        return text;
    }

    private static JsonObject Schema(params (string Name, JsonObject Property)[] properties) => Schema([], properties);

    private static JsonObject Schema(string[] required, params (string Name, JsonObject Property)[] properties)
    {
        var props = new JsonObject();
        foreach (var (name, property) in properties) props[name] = property;
        var schema = new JsonObject { ["type"] = "object", ["properties"] = props, ["additionalProperties"] = false };
        if (required.Length > 0) schema["required"] = new JsonArray(required.Select(item => (JsonNode)item).ToArray());
        return schema;
    }

    private static JsonObject Day(string description) => new() { ["type"] = "string", ["pattern"] = DayPattern, ["description"] = description };

    private static JsonObject Int(string description) => new() { ["type"] = "integer", ["description"] = description };

    private static JsonObject Text(string description) => new() { ["type"] = "string", ["description"] = description };
}

internal sealed class ToolArgumentException(string message) : ArgumentException(message);
