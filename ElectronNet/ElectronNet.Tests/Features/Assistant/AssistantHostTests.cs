using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using ElectronNet.Features.Assistant;
using ElectronNet.Features.LocalApi;
using ElectronNet.Features.Semantic;
using ElectronNet.Models;
using ElectronNet.Persistence;
using ElectronNet.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Core.Environment;
using SteamStat.Core.Settings;

namespace ElectronNet.Tests.Features.Assistant;

[TestFixture]
public class AssistantHostTests
{
    private const string SteamId = "76561198000000001";
    private string _directory = null!;
    private AppPaths _paths = null!;
    private MemorySettings _settings = null!;
    private SemanticLayerService _semantic = null!;
    private ArchiveToolCatalog _tools = null!;
    private CommandRunner _commands = null!;
    private AuditLog _audit = null!;
    private AppEnvironment _environment = null!;
    private readonly FixedTime _time = new(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));

    private static readonly IReadOnlySet<string> AllTiers = SemanticTier.All.ToHashSet();
    private static readonly IReadOnlySet<string> AggregateOnly = new HashSet<string> { SemanticTier.Aggregate };

    [SetUp]
    public async Task SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "steam-stat-assistant-tests", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_directory);
        Directory.CreateDirectory(_paths.DatabaseDirectory);
        var factory = new TestFactory(_paths.DatabaseFile);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            db.SteamUserTable.Add(new SteamUser { SteamId = SteamId, AccountName = "secret-login", PersonaName = "Tester" });
            db.SteamAppTable.Add(new SteamApp { AppId = 570, Name = "Dota 2" });
            db.SteamAppTable.Add(new SteamApp { AppId = 620, Name = "Portal 2" });
            var start = (int)_time.GetUtcNow().AddDays(-2).ToUnixTimeSeconds();
            db.UseAppRecordTable.AddRange(
                new UseAppRecord { AppId = 570, SteamId = SteamId, StartTime = start, EndTime = start + 7200, Duration = 7200 },
                new UseAppRecord { AppId = 620, SteamId = SteamId, StartTime = start + 10000, EndTime = start + 13600, Duration = 3600 });
            await db.SaveChangesAsync();
        }
        _settings = new MemorySettings();
        _semantic = new SemanticLayerService(_paths, _settings, NullLogger<SemanticLayerService>.Instance);
        await _semantic.InstallAsync();
        var devices = new DeviceService(factory, _time, NullLogger<DeviceService>.Instance);
        var records = new UseAppRecordService(factory, _time, devices, _settings, NullLogger<UseAppRecordService>.Instance);
        _tools = new ArchiveToolCatalog(_semantic, records, _settings, _time);
        _environment = new AppEnvironment(false, "en-US", false, _paths, "9.9.9");
        _commands = new CommandRunner(_tools, _environment);
        _audit = new AuditLog(_paths, _time, NullLogger<AuditLog>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private static ToolContext Context(IReadOnlySet<string> tiers, bool mask = false) => new(tiers, mask, 100, [], false);

    #region 工具

    [Test]
    public async Task EveryTool_RunsThroughTheGatewayWithAllScopes()
    {
        var arguments = new Dictionary<string, JsonObject>
        {
            ["find_games"] = new() { ["query"] = "dota" },
            ["get_game"] = new() { ["app_id"] = 570 },
            ["run_sql"] = new() { ["sql"] = "SELECT COUNT(*) FROM v_session" },
            ["get_top_games"] = new() { ["from"] = "2026-02-01", ["to"] = "2026-03-01" }
        };
        foreach (var tool in _tools.All)
        {
            var table = await _tools.ExecuteAsync(tool.Name, arguments.GetValueOrDefault(tool.Name) ?? [], Context(AllTiers));
            table.Error.Should().BeNull(tool.Name);
        }
    }

    [Test]
    public async Task Tools_ReturnRecordedNumbers()
    {
        var top = await _tools.ExecuteAsync("get_top_games", [], Context(AllTiers));
        top.Rows.Select(row => row[1]).Should().Equal("Dota 2", "Portal 2");

        var overview = await _tools.ExecuteAsync("get_overview", [], Context(AggregateOnly));
        var allTime = overview.Rows.Single(row => (string?)row[0] == "all_time");
        Convert.ToInt64(allTime[1]).Should().Be(10800);
    }

    [Test]
    public async Task UnauthorizedScopes_AreRefusedByTheToolAndTheGateway()
    {
        (await _tools.ExecuteAsync("get_top_games", [], Context(AggregateOnly))).Error.Should().StartWith("not_authorized");
        (await _tools.ExecuteAsync("run_sql", new JsonObject { ["sql"] = "SELECT app_name FROM v_session" }, Context(AggregateOnly))).Error.Should().StartWith("denied");
        (await _tools.ExecuteAsync("run_sql", new JsonObject { ["sql"] = "SELECT * FROM steam_login_token" }, Context(AllTiers))).Error.Should().StartWith("denied");
        _tools.Available(AggregateOnly).Select(tool => tool.Tier).Should().OnlyContain(tier => tier == SemanticTier.Aggregate);

        var views = await _tools.ExecuteAsync("describe_views", [], Context(AggregateOnly));
        views.Rows.Select(row => row[1]).Should().OnlyContain(tier => (string?)tier == SemanticTier.Aggregate);
    }

    [Test]
    public async Task SteamIds_AreReplacedWithAliasesWhenMasked()
    {
        var table = await _tools.ExecuteAsync("run_sql", new JsonObject { ["sql"] = "SELECT steam_id FROM v_session" }, Context(AllTiers, mask: true));

        table.Rows.Should().OnlyContain(row => (string?)row[0] == "player-1");
    }

    [Test]
    public async Task HiddenGames_DoNotAppear()
    {
        _settings.Current.Analytics = new AnalyticsSettings { HiddenAppIds = [620] };
        await _semantic.HandleAsync(new ElectronNet.Hosting.AppSettingsChanged(null!), CancellationToken.None);

        var top = await _tools.ExecuteAsync("get_top_games", [], Context(AllTiers));

        top.Rows.Select(row => row[1]).Should().Equal("Dota 2");
    }

    [Test]
    public async Task BadArguments_AreReportedNotThrown()
    {
        (await _tools.ExecuteAsync("get_daily_playtime", new JsonObject { ["from"] = "yesterday" }, Context(AllTiers))).Error.Should().StartWith("invalid_arguments");
        (await _tools.ExecuteAsync("get_game", [], Context(AllTiers))).Error.Should().StartWith("invalid_arguments");
        (await _tools.ExecuteAsync("nope", [], Context(AllTiers))).Error.Should().StartWith("unknown_tool");
    }

    [Test]
    public void ToolResults_AreTrimmedForTheModel()
    {
        var rows = Enumerable.Range(0, 5000).Select(index => new object?[] { index, new string('x', 40) }).ToList();
        var (content, truncated) = AssistantService.ForModel(new ToolTable(["a", "b"], rows));

        truncated.Should().BeTrue();
        content.Length.Should().BeLessThanOrEqualTo(24_000);
        JsonNode.Parse(content)!["truncated"]!.GetValue<bool>().Should().BeTrue();
    }

    #endregion

    #region 命令

    [Test]
    public async Task Commands_FormatTextAndJson()
    {
        var top = await _commands.RunAsync(["top", "5"], Context(AllTiers));
        top.ExitCode.Should().Be(0);
        top.Output.Should().Contain("Dota 2");

        var json = await _commands.RunAsync(["today", "--json"], Context(AllTiers));
        JsonNode.Parse(json.Output)!["day"]!.GetValue<string>().Should().Be("2026-03-01");

        (await _commands.RunAsync(["frobnicate"], Context(AllTiers))).ExitCode.Should().Be(CommandRunner.ExitUsage);
        (await _commands.RunAsync(["top"], Context(AggregateOnly))).ExitCode.Should().Be(CommandRunner.ExitToolError);
        (await _commands.RunAsync(["clear"], Context(AllTiers))).Clear.Should().BeTrue();
    }

    [Test]
    public void TerminalArguments_KeepQuotedSql()
        => IpcMainService.TerminalArguments("steamstat sql \"SELECT 1 AS x\" --json").Should().Equal("sql", "SELECT 1 AS x", "--json");

    [Test]
    public void CommandTable_AlignsWideCharacters()
        => CommandRunner.DisplayWidth("塞尔达ab").Should().Be(8);

    #endregion

    #region 审计日志

    [Test]
    public void AuditLog_StoresListsAndClears()
    {
        var first = _audit.Append(AuditChannels.Ai, "ask", "api.example · m", "question one", ["aggregate"], "{\"body\":1}");
        var second = _audit.Append(AuditChannels.Api, "query", "127.0.0.1", "select", ["aggregate", "games"], new string('x', AuditLog.MaxContentBytes + 10));

        second.Should().BeGreaterThan(first);
        _audit.List(null, 10, null).Select(entry => entry.Id).Should().Equal(second, first);
        _audit.List(null, 10, AuditChannels.Ai).Should().ContainSingle();
        _audit.List(second, 10, null).Should().ContainSingle().Which.Id.Should().Be(first);
        _audit.Get(first)!.Content.Should().Be("{\"body\":1}");
        _audit.Get(second)!.Summary.Truncated.Should().BeTrue();

        _audit.Clear().Should().BeTrue();
        _audit.List(null, 10, null).Should().BeEmpty();
    }

    #endregion

    #region 本地接口

    private LocalApiService CreateApi(Action<LocalApiSettings>? configure = null)
    {
        var settings = new LocalApiSettings { Enabled = true, Port = 47615, Scopes = ["aggregate"], McpEnabled = true, MetricsEnabled = true, OverlayEnabled = true };
        configure?.Invoke(settings);
        _settings.Current.LocalApi = settings;
        return new LocalApiService(_settings, _paths, _environment, _tools, _commands, _semantic, _audit, NullLogger<LocalApiService>.Instance);
    }

    private static HttpRequestData Request(string method, string path, string? token, string host = "127.0.0.1:47615", string? origin = null, string body = "")
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Host"] = host };
        if (token != null) headers["Authorization"] = $"Bearer {token}";
        if (origin != null) headers["Origin"] = origin;
        var question = path.IndexOf('?');
        var query = new Dictionary<string, string>();
        if (question >= 0)
            foreach (var pair in path[(question + 1)..].Split('&'))
                query[pair.Split('=')[0]] = Uri.UnescapeDataString(pair.Split('=')[1]);
        return new HttpRequestData(method, question >= 0 ? path[..question] : path, query, headers, Encoding.UTF8.GetBytes(body));
    }

    [Test]
    public async Task LocalApi_ChecksHostOriginAndToken()
    {
        await using var api = CreateApi();
        var token = api.Token;

        (await api.HandleRequestAsync(Request("GET", "/v1/ping", null), default)).Status.Should().Be(200);
        (await api.HandleRequestAsync(Request("GET", "/v1/tools", token, host: "evil.example:47615"), default)).Status.Should().Be(421);
        (await api.HandleRequestAsync(Request("GET", "/v1/tools", token, origin: "https://evil.example"), default)).Status.Should().Be(403);
        (await api.HandleRequestAsync(Request("GET", "/v1/tools", null), default)).Status.Should().Be(401);
        (await api.HandleRequestAsync(Request("GET", "/v1/tools", token + "x"), default)).Status.Should().Be(401);
        (await api.HandleRequestAsync(Request("GET", "/v1/tools", token, origin: "http://127.0.0.1:47615"), default)).Status.Should().Be(200);
        (await api.HandleRequestAsync(Request("GET", $"/metrics?token={Uri.EscapeDataString(token)}", null), default)).Status.Should().Be(200);
        // 查询参数里的 token 只给只读的 GET（OBS 浏览器源）
        (await api.HandleRequestAsync(Request("POST", $"/v1/cli?token={Uri.EscapeDataString(token)}", null, body: "{}"), default)).Status.Should().Be(401);
        token.Should().StartWith(LocalApiService.TokenPrefix);
    }

    [Test]
    public async Task LocalApi_EnforcesItsOwnScopesAndAuditsResponses()
    {
        await using var api = CreateApi();
        var token = api.Token;

        var denied = await api.HandleRequestAsync(Request("GET", "/v1/tools/get_top_games", token), default);
        denied.Status.Should().Be(403);

        var overview = await api.HandleRequestAsync(Request("POST", "/v1/tools/get_overview", token, body: "{}"), default);
        overview.Status.Should().Be(200);
        JsonNode.Parse(overview.Body)!["items"]!.AsArray().Should().NotBeEmpty();

        var cli = await api.HandleRequestAsync(Request("POST", "/v1/cli", token, body: """{"args":["today"]}"""), default);
        JsonNode.Parse(cli.Body)!["exitCode"]!.GetValue<int>().Should().Be(0);

        _audit.List(null, 10, AuditChannels.Api).Select(entry => entry.Kind).Should().Contain(["get_top_games", "get_overview", "cli"]);
    }

    [Test]
    public async Task LocalApi_ServesMcpAndHidesUnauthorizedTools()
    {
        await using var api = CreateApi();
        var response = await api.HandleRequestAsync(Request("POST", "/mcp", api.Token, body: """{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""), default);

        var names = JsonNode.Parse(response.Body)!["result"]!["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>()).ToList();
        names.Should().Contain("get_overview").And.NotContain("get_top_games");

        var notification = await api.HandleRequestAsync(Request("POST", "/mcp", api.Token, body: """{"jsonrpc":"2.0","method":"notifications/initialized"}"""), default);
        notification.Status.Should().Be(202);
        (await api.HandleRequestAsync(Request("GET", "/mcp", api.Token), default)).Status.Should().Be(405);
    }

    [Test]
    public async Task LocalApi_TurnsOffSubFeatures()
    {
        await using var api = CreateApi(settings => { settings.McpEnabled = false; settings.MetricsEnabled = false; settings.OverlayEnabled = false; });

        (await api.HandleRequestAsync(Request("POST", "/mcp", api.Token, body: "{}"), default)).Status.Should().Be(404);
        (await api.HandleRequestAsync(Request("GET", "/metrics", api.Token), default)).Status.Should().Be(404);
        (await api.HandleRequestAsync(Request("GET", "/overlay", api.Token), default)).Status.Should().Be(404);
    }

    [Test]
    public async Task LocalApi_OverlayAndMetricsRespectPrivacy()
    {
        await using var api = CreateApi();
        var page = await api.HandleRequestAsync(Request("GET", "/overlay", api.Token), default);
        page.Headers!["Content-Security-Policy"].Should().Contain("default-src 'none'");

        var metrics = Encoding.UTF8.GetString((await api.HandleRequestAsync(Request("GET", "/metrics", api.Token), default)).Body);
        metrics.Should().Contain("steamstat_playtime_total_seconds 10800");
        metrics.Should().NotContain("Dota 2");

        _settings.Current.LocalApi!.Scopes = ["aggregate", "games"];
        var withGames = Encoding.UTF8.GetString((await api.HandleRequestAsync(Request("GET", "/metrics", api.Token), default)).Body);
        withGames.Should().Contain("game=\"Dota 2\"");
    }

    [Test]
    public async Task LocalApi_WritesTheStateFileForTheCli()
    {
        await using var api = CreateApi(settings => settings.Port = FreePort());
        await api.ApplyAsync();

        var state = JsonNode.Parse(await File.ReadAllTextAsync(api.StateFile))!;
        state["running"]!.GetValue<bool>().Should().BeTrue();
        state["token"]!.GetValue<string>().Should().Be(api.Token);

        _settings.Current.LocalApi!.Enabled = false;
        await api.ApplyAsync();
        JsonNode.Parse(await File.ReadAllTextAsync(api.StateFile))!["running"]!.GetValue<bool>().Should().BeFalse();
    }

    [Test]
    public async Task LoopbackServer_AnswersOverARealSocket()
    {
        await using var server = new LoopbackHttpServer(0, (request, _) => Task.FromResult(HttpResponseData.Text(200, $"{request.Method} {request.Path} {request.BodyText}")), NullLogger.Instance);
        server.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(System.Net.IPAddress.Loopback, server.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"POST /x?a=1 HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\nContent-Length: 2\r\n\r\nhi"));
        var reply = await new StreamReader(stream).ReadToEndAsync();

        reply.Should().StartWith("HTTP/1.1 200 OK").And.EndWith("POST /x hi").And.Contain("Connection: close");
    }

    [TestCase("GET / HTTP/1.1\r\nHost: a\r\nTransfer-Encoding: chunked\r\n\r\n", 411)]
    [TestCase("GET / HTTP/1.1\r\nHost: a\r\nHost: b\r\n\r\n", 400)]
    [TestCase("GET / HTTP/1.1\r\nContent-Length: 99999999\r\n\r\n", 413)]
    [TestCase("garbage\r\n\r\n", 400)]
    public async Task RequestParser_RejectsDangerousRequests(string raw, int status)
    {
        var act = () => LoopbackHttpServer.ReadAsync(new MemoryStream(Encoding.ASCII.GetBytes(raw)), default);
        (await act.Should().ThrowAsync<HttpParseException>()).Which.Status.Should().Be(status);
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    #endregion

    private sealed class TestFactory(string databaseFile) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(SqliteConnectionStrings.Create(databaseFile, pooling: false)).Options);
    }

    private sealed class MemorySettings : ISettingsStore
    {
        public AppSettings Current { get; } = new();
        public AppSettings GetSettings() => Current;
        public Task<bool> UpdateAsync(AppSettings partialSettings, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> ResetAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
