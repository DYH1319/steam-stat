using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ElectronNet.Features.Assistant;
using ElectronNet.Features.Semantic;
using ElectronNet.Hosting;
using ElectronNet.Services;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Assistant;
using SteamStat.Core.Environment;
using SteamStat.Core.Events;
using SteamStat.Core.Settings;

namespace ElectronNet.Features.LocalApi;

/// <summary>
/// 本地只读接口：MCP（/mcp）、REST（/v1/*）、Prometheus（/metrics）、OBS 叠加层（/overlay）和命令行（/v1/cli）。
/// <list type="bullet">
/// <item>只监听 127.0.0.1 / ::1，默认关闭；局域网访问是第十二节的另一套设计，不在这里。</item>
/// <item>除了 /v1/ping，每个请求都要 token：Authorization: Bearer，或只读的 GET 用 ?token=（OBS 浏览器源没法加请求头）。</item>
/// <item>Host 头只认环回名字（防 DNS 重绑定），带 Origin 的请求只认本服务自己（防网页跨站调用）。</item>
/// <item>数据经同一套工具和语义视图网关，分级授权在设置里单独配，默认只有聚合统计；主播模式下好友不出门、SteamID 换成代号。</item>
/// <item>工具调用、SQL、命令行和 MCP 的 tools/call 的返回内容写进审计日志；叠加层和 /metrics 内容固定，不记。</item>
/// </list>
/// token 和端口写在 {UserData}/local-api.json，命令行工具从这里找到服务。token 不在设置文件里，所以不进备份。
/// </summary>
internal sealed class LocalApiService(
    ISettingsStore settingsStore,
    IAppPaths appPaths,
    AppEnvironment environment,
    ArchiveToolCatalog tools,
    CommandRunner commands,
    SemanticLayerService semantic,
    AuditLog audit,
    ILogger<LocalApiService> logger) : IEventHandler<AppSettingsChanged>, IAsyncDisposable
{
    internal const string TokenPrefix = "ss_";
    private const int ApiMaxRows = 1000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private LoopbackHttpServer? _server;
    private string? _error;
    private string? _token;

    internal string StateFile => Path.Combine(appPaths.UserDataDirectory, "local-api.json");

    private LocalApiSettings Settings => settingsStore.GetSettings().LocalApi ?? new LocalApiSettings();

    public async Task StartAsync(CancellationToken cancellationToken = default) => await ApplyAsync(cancellationToken).ConfigureAwait(false);

    public Task HandleAsync(AppSettingsChanged message, CancellationToken cancellationToken) => ApplyAsync(cancellationToken);

    /// <summary>按设置开关服务；端口变了就重启。</summary>
    internal async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = Settings;
            var port = settings.Port ?? LocalApiSettings.DefaultPort;
            if (settings.Enabled != true)
            {
                await StopAsync().ConfigureAwait(false);
                _error = null;
                WriteState(running: false);
                return;
            }
            if (_server != null && _server.Port == port) return;
            await StopAsync().ConfigureAwait(false);
            var server = new LoopbackHttpServer(port, HandleRequestAsync, logger);
            try
            {
                server.Start();
                _server = server;
                _error = null;
                logger.LogInformation("Local API is listening on 127.0.0.1:{Port}", server.Port);
            }
            catch (SocketException exception)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                _error = exception.SocketErrorCode == SocketError.AddressAlreadyInUse ? "portInUse" : "failed";
                logger.LogWarning(exception, "Local API could not listen on port {Port}", port);
            }
            WriteState(running: _server != null);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopAsync()
    {
        if (_server == null) return;
        await _server.DisposeAsync().ConfigureAwait(false);
        _server = null;
    }

    // ------------------------------------------------------------------
    // token 与状态文件
    // ------------------------------------------------------------------

    internal string Token
    {
        get
        {
            if (_token != null) return _token;
            try
            {
                if (File.Exists(StateFile)
                    && JsonNode.Parse(File.ReadAllText(StateFile))?["token"] is JsonValue value
                    && value.TryGetValue<string>(out var stored)
                    && stored.StartsWith(TokenPrefix, StringComparison.Ordinal) && stored.Length >= 40)
                    return _token = stored;
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Failed to read the local API state; generating a new token");
            }
            _token = NewToken();
            WriteState(_server != null);
            return _token;
        }
    }

    /// <summary>非十六进制前缀：token 会经 IPC 显示在设置页，桥接层不会把它当成 GUID 改写。</summary>
    private static string NewToken() => TokenPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async Task<LocalApiStatusDto> RegenerateTokenAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _token = NewToken();
            WriteState(_server != null);
        }
        finally
        {
            _gate.Release();
        }
        return GetStatus();
    }

    private void WriteState(bool running)
    {
        try
        {
            Directory.CreateDirectory(appPaths.UserDataDirectory);
            var state = new JsonObject
            {
                ["token"] = _token ?? Token,
                ["port"] = Settings.Port ?? LocalApiSettings.DefaultPort,
                ["running"] = running,
                ["version"] = environment.AppVersion
            };
            var temporary = StateFile + ".tmp";
            File.WriteAllText(temporary, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            LocalFiles.RestrictToCurrentUser(temporary);
            File.Move(temporary, StateFile, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Failed to write the local API state file");
        }
    }

    public LocalApiStatusDto GetStatus()
    {
        var settings = Settings;
        var port = _server?.Port ?? settings.Port ?? LocalApiSettings.DefaultPort;
        var baseUrl = $"http://127.0.0.1:{port}";
        var token = Token;
        return new LocalApiStatusDto
        {
            Enabled = settings.Enabled == true,
            Running = _server != null,
            Port = port,
            Error = _error,
            BaseUrl = baseUrl,
            Token = token,
            OverlayUrl = $"{baseUrl}/overlay?token={Uri.EscapeDataString(token)}",
            McpUrl = $"{baseUrl}/mcp",
            MetricsUrl = $"{baseUrl}/metrics",
            CliPath = CliPath(),
            EffectiveScopes = ExternalScopes.All.Where(Scopes().Contains).ToList()
        };
    }

    /// <summary>命令行工具和 Host 发布在同一个目录。开发模式下一般没有，返回空。</summary>
    private static string? CliPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "steamstat.exe" : "steamstat");
        return File.Exists(path) ? path : null;
    }

    private IReadOnlySet<string> Scopes()
    {
        var settings = settingsStore.GetSettings();
        return ExternalScopes.Effective(settings.LocalApi?.Scopes, settings.StreamerMode == true);
    }

    private ToolContext Context(bool chinese)
    {
        var settings = settingsStore.GetSettings();
        return new ToolContext(Scopes(), settings.StreamerMode == true, ApiMaxRows, AccountSettings.MineOf(settings), chinese);
    }

    private bool PrefersChinese(HttpRequestData request)
        => request.Query.TryGetValue("lang", out var lang)
            ? lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            : (settingsStore.GetSettings().Language ?? environment.Locale).StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------
    // 请求
    // ------------------------------------------------------------------

    internal async Task<HttpResponseData> HandleRequestAsync(HttpRequestData request, CancellationToken cancellationToken)
    {
        var port = _server?.Port ?? Settings.Port ?? LocalApiSettings.DefaultPort;
        if (!IsLoopbackHost(request.Header("Host"), port)) return HttpResponseData.Text(421, "Unexpected Host header");
        if (request.Header("Origin") is { } origin && !IsOwnOrigin(origin, port)) return HttpResponseData.Text(403, "Cross-origin requests are not allowed");

        if (request.Path is "/" or "/v1/ping")
            return HttpResponseData.Json(200, new JsonObject { ["app"] = "steam-stat", ["version"] = environment.AppVersion }.ToJsonString());
        if (!Authorized(request)) return HttpResponseData.Json(401, """{"error":"unauthorized"}""");

        var settings = Settings;
        try
        {
            return request.Path switch
            {
                "/mcp" when settings.McpEnabled != true => NotFound(),
                "/mcp" => await McpAsync(request, cancellationToken).ConfigureAwait(false),
                "/metrics" when settings.MetricsEnabled != true => NotFound(),
                "/metrics" => request.Method == "GET" ? await MetricsAsync(cancellationToken).ConfigureAwait(false) : MethodNotAllowed("GET"),
                "/overlay" when settings.OverlayEnabled != true => NotFound(),
                "/overlay" => request.Method == "GET" ? new HttpResponseData(200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(OverlayPage.Html), OverlayPage.Headers) : MethodNotAllowed("GET"),
                "/overlay/data" when settings.OverlayEnabled != true => NotFound(),
                "/overlay/data" => request.Method == "GET" ? await OverlayDataAsync(cancellationToken).ConfigureAwait(false) : MethodNotAllowed("GET"),
                "/v1/tools" => request.Method == "GET" ? ListTools() : MethodNotAllowed("GET"),
                "/v1/query" => request.Method == "POST" ? await QueryAsync(request, cancellationToken).ConfigureAwait(false) : MethodNotAllowed("POST"),
                "/v1/cli" => request.Method == "POST" ? await CliAsync(request, cancellationToken).ConfigureAwait(false) : MethodNotAllowed("POST"),
                _ when request.Path.StartsWith("/v1/tools/", StringComparison.Ordinal) => await ToolAsync(request, request.Path["/v1/tools/".Length..], cancellationToken).ConfigureAwait(false),
                _ => NotFound()
            };
        }
        catch (JsonException)
        {
            return HttpResponseData.Json(400, """{"error":"invalid_json"}""");
        }
    }

    internal static bool IsLoopbackHost(string? host, int port)
        => host != null && (host.Equals($"127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase)
                            || host.Equals($"localhost:{port}", StringComparison.OrdinalIgnoreCase)
                            || host.Equals($"[::1]:{port}", StringComparison.OrdinalIgnoreCase));

    internal static bool IsOwnOrigin(string origin, int port)
        => origin.Equals($"http://127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase)
           || origin.Equals($"http://localhost:{port}", StringComparison.OrdinalIgnoreCase)
           || origin.Equals($"http://[::1]:{port}", StringComparison.OrdinalIgnoreCase);

    private bool Authorized(HttpRequestData request)
    {
        string? presented = null;
        if (request.Header("Authorization") is { } authorization && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            presented = authorization["Bearer ".Length..].Trim();
        else if (request.Method == "GET" && request.Query.TryGetValue("token", out var query))
            presented = query;
        if (string.IsNullOrEmpty(presented)) return false;
        var expected = Encoding.UTF8.GetBytes(Token);
        var actual = Encoding.UTF8.GetBytes(presented);
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static HttpResponseData NotFound() => HttpResponseData.Json(404, """{"error":"not_found"}""");

    private static HttpResponseData MethodNotAllowed(string allow)
        => new(405, "application/json; charset=utf-8", """{"error":"method_not_allowed"}"""u8.ToArray(), new Dictionary<string, string> { ["Allow"] = allow });

    private static JsonObject ParseObject(HttpRequestData request)
        => request.Body.Length == 0 ? [] : JsonNode.Parse(request.Body) as JsonObject ?? throw new JsonException("Expected an object");

    private static string Client(HttpRequestData request)
        => AuditLog.Shorten($"127.0.0.1 · {request.Header("User-Agent") ?? "unknown client"}", 80);

    private HttpResponseData ListTools()
    {
        var scopes = Scopes();
        var list = new JsonArray(tools.Available(scopes).Select(tool => (JsonNode)new JsonObject
        {
            ["name"] = tool.Name,
            ["title"] = tool.Title,
            ["scope"] = tool.Tier,
            ["description"] = tool.Description,
            ["inputSchema"] = tool.Schema.DeepClone()
        }).ToArray());
        return HttpResponseData.Json(200, new JsonObject { ["tools"] = list, ["scopes"] = new JsonArray(ExternalScopes.All.Where(scopes.Contains).Select(item => (JsonNode)item).ToArray()) }.ToJsonString());
    }

    private async Task<HttpResponseData> ToolAsync(HttpRequestData request, string name, CancellationToken cancellationToken)
    {
        if (request.Method is not ("GET" or "POST")) return MethodNotAllowed("GET, POST");
        if (tools.Find(name) == null) return NotFound();
        JsonObject arguments;
        if (request.Method == "POST") arguments = ParseObject(request);
        else
        {
            arguments = [];
            foreach (var (key, value) in request.Query.Where(pair => pair.Key is not ("token" or "lang"))) arguments[key] = value;
        }
        var context = Context(PrefersChinese(request));
        var table = await tools.ExecuteAsync(name, arguments, context, cancellationToken).ConfigureAwait(false);
        return Table(request, name, arguments.ToJsonString(), table, context);
    }

    private async Task<HttpResponseData> QueryAsync(HttpRequestData request, CancellationToken cancellationToken)
    {
        var body = ParseObject(request);
        var context = Context(PrefersChinese(request));
        var table = await tools.ExecuteAsync("run_sql", body, context, cancellationToken).ConfigureAwait(false);
        return Table(request, "query", body.ToJsonString(), table, context);
    }

    private HttpResponseData Table(HttpRequestData request, string kind, string summary, ToolTable table, ToolContext context)
    {
        var json = table.ToJson();
        if (table.Error == null) json["items"] = table.ToObjects();
        var body = json.ToJsonString();
        audit.Append(AuditChannels.Api, kind, Client(request), summary, ExternalScopes.All.Where(context.Tiers.Contains), body);
        var status = table.Error switch
        {
            null => 200,
            var error when error.StartsWith("not_authorized", StringComparison.Ordinal) || error.StartsWith("denied", StringComparison.Ordinal) => 403,
            _ => 400
        };
        return HttpResponseData.Json(status, body);
    }

    private async Task<HttpResponseData> CliAsync(HttpRequestData request, CancellationToken cancellationToken)
    {
        var body = ParseObject(request);
        var args = (body["args"] as JsonArray ?? [])
            .Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text : item?.ToString() ?? string.Empty)
            .Take(64)
            .ToList();
        var chinese = body["lang"] is JsonValue lang && lang.TryGetValue<string>(out var language)
            ? language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            : PrefersChinese(request);
        var context = Context(chinese);
        var result = await commands.RunAsync(args, context, cancellationToken).ConfigureAwait(false);
        var response = new JsonObject { ["exitCode"] = result.ExitCode, ["output"] = result.Output }.ToJsonString();
        audit.Append(AuditChannels.Api, "cli", Client(request), "steamstat " + string.Join(' ', args), ExternalScopes.All.Where(context.Tiers.Contains), result.Output);
        return HttpResponseData.Json(200, response);
    }

    private async Task<HttpResponseData> McpAsync(HttpRequestData request, CancellationToken cancellationToken)
    {
        if (request.Method != "POST") return MethodNotAllowed("POST");
        var host = new McpHost(tools, audit, request, Context(PrefersChinese(request)));
        var response = await McpProtocol.HandleAsync(request.BodyText, host,
            new McpServerInfo("steam-stat", environment.AppVersion ?? "dev",
                "Read-only access to the user's local Steam Stat archive: playtime, games, sessions, achievements and time with friends. " +
                "Only the scopes the user granted in Steam Stat are available; other tools are hidden. Durations are in seconds and hours, " +
                "days are logical days in the user's local time zone. Call describe_views before run_sql."),
            cancellationToken).ConfigureAwait(false);
        return response == null ? HttpResponseData.Empty(202) : HttpResponseData.Json(200, response);
    }

    private sealed class McpHost(ArchiveToolCatalog catalog, AuditLog log, HttpRequestData request, ToolContext context) : IMcpToolHost
    {
        public IReadOnlyList<ToolSpec> Tools { get; } = catalog.Available(context.Tiers).Select(ArchiveToolCatalog.Spec).ToList();

        public async Task<McpToolResult> CallAsync(string name, JsonObject arguments, CancellationToken cancellationToken)
        {
            var table = await catalog.ExecuteAsync(name, arguments, context, cancellationToken).ConfigureAwait(false);
            var text = table.ToJson().ToJsonString();
            log.Append(AuditChannels.Mcp, name, Client(request), arguments.ToJsonString(), ExternalScopes.All.Where(context.Tiers.Contains), text);
            return new McpToolResult(text, table.Error != null);
        }
    }

    // ------------------------------------------------------------------
    // Prometheus 与 OBS 叠加层
    // ------------------------------------------------------------------

    private async Task<HttpResponseData> MetricsAsync(CancellationToken cancellationToken)
    {
        var context = Context(false);
        var metrics = new List<PrometheusMetric>
        {
            new("steamstat_info", "gauge", "Steam Stat version.", [new([new("version", environment.AppVersion ?? "dev")], 1)])
        };
        var overview = await tools.ExecuteAsync("get_overview", [], context, cancellationToken).ConfigureAwait(false);
        if (overview.Error == null)
        {
            foreach (var row in overview.Rows)
            {
                var name = row[0] switch
                {
                    "today" => "steamstat_playtime_today_seconds",
                    "last_7_days" => "steamstat_playtime_last_7_days_seconds",
                    "this_month" => "steamstat_playtime_month_seconds",
                    "this_year" => "steamstat_playtime_year_seconds",
                    _ => "steamstat_playtime_total_seconds"
                };
                metrics.Add(new(name, "gauge", $"Playtime ({row[0]}), including running sessions for today.", [new([], Convert.ToDouble(row[1], System.Globalization.CultureInfo.InvariantCulture))]));
                if ((string?)row[0] == "all_time")
                    metrics.Add(new("steamstat_active_days", "gauge", "Days with any recorded play.", [new([], Convert.ToDouble(row[3], System.Globalization.CultureInfo.InvariantCulture))]));
            }
        }
        // 游戏名是标签值，只有授权了游戏级别才输出
        if (context.Tiers.Contains(SemanticTier.Games))
        {
            var now = await tools.ExecuteAsync("get_now_playing", [], context, cancellationToken).ConfigureAwait(false);
            metrics.Add(new("steamstat_sessions_running", "gauge", "Games running right now.", [new([], now.Rows.Count)]));
            metrics.Add(new("steamstat_now_playing", "gauge", "1 for each game that is running.",
                now.Rows.Select(row => new PrometheusSample([new("app_id", Convert.ToString(row[0], System.Globalization.CultureInfo.InvariantCulture) ?? ""), new("game", Convert.ToString(row[1], System.Globalization.CultureInfo.InvariantCulture) ?? "")], 1)).ToList()));
            var top = await tools.ExecuteAsync("get_top_games", new JsonObject { ["limit"] = 25 }, context, cancellationToken).ConfigureAwait(false);
            if (top.Error == null)
                metrics.Add(new("steamstat_game_playtime_seconds", "gauge", "All-time playtime of the 25 most played games.",
                    top.Rows.Select(row => new PrometheusSample([new("app_id", Convert.ToString(row[0], System.Globalization.CultureInfo.InvariantCulture) ?? ""), new("game", Convert.ToString(row[1], System.Globalization.CultureInfo.InvariantCulture) ?? "")], Convert.ToDouble(row[3], System.Globalization.CultureInfo.InvariantCulture))).ToList()));
        }
        return new HttpResponseData(200, PrometheusText.ContentType, Encoding.UTF8.GetBytes(PrometheusText.Write(metrics)));
    }

    /// <summary>
    /// 叠加层是用户主动打开、放在直播画面上的东西，所以显示当前游戏名，和接口的授权级别无关；
    /// 隐藏游戏不显示，主播模式下账号昵称换成「玩家」。
    /// </summary>
    private async Task<HttpResponseData> OverlayDataAsync(CancellationToken cancellationToken)
    {
        var settings = settingsStore.GetSettings();
        var streamer = settings.StreamerMode == true;
        var overlay = new ToolContext(new HashSet<string> { SemanticTier.Aggregate, SemanticTier.Games }, true, 20, AccountSettings.MineOf(settings), false);
        var overview = await tools.ExecuteAsync("get_overview", [], overlay, cancellationToken).ConfigureAwait(false);
        var todaySeconds = overview.Error == null && overview.Rows.Count > 0 ? Convert.ToInt64(overview.Rows[0][1], System.Globalization.CultureInfo.InvariantCulture) : 0;
        var hidden = (settings.Analytics?.HiddenAppIds ?? []).ToHashSet();
        var playing = new JsonArray();
        foreach (var session in tools.OngoingSessions(overlay).Where(session => !hidden.Contains(session.AppId)))
        {
            var item = new JsonObject
            {
                ["appId"] = session.AppId,
                ["name"] = session.AppName,
                ["startedAt"] = session.StartTime,
                ["player"] = streamer ? null : session.UserPersonaName
            };
            var progress = await semantic.QueryAsync(
                "SELECT unlocked, total FROM v_achievement_game WHERE app_id = $app AND steam_id = $sid LIMIT 1",
                overlay.Tiers, new Dictionary<string, object?> { ["$app"] = session.AppId, ["$sid"] = session.SteamId }, 1, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (progress.Failure == SemanticQueryFailure.None && progress.Rows is [{ } row] && row[1] is long total && total > 0)
                item["achievements"] = new JsonObject { ["unlocked"] = Convert.ToInt64(row[0], System.Globalization.CultureInfo.InvariantCulture), ["total"] = total };
            playing.Add(item);
        }
        return HttpResponseData.Json(200, new JsonObject
        {
            ["now"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["streamerMode"] = streamer,
            ["todaySeconds"] = todaySeconds,
            ["playing"] = playing,
            ["language"] = settings.Language ?? environment.Locale
        }.ToJsonString());
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        try
        {
            WriteState(running: false);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
        {
            // 退出时设置已经释放，状态文件下次启动会重写
        }
        _gate.Dispose();
    }
}
