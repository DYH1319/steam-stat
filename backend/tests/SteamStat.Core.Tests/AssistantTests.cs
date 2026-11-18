using System.Text.Json.Nodes;
using FluentAssertions;
using SteamStat.Core.Assistant;
using SteamStat.Core.Settings;

namespace SteamStat.Core.Tests;

[TestFixture]
public class AssistantTests
{
    private static readonly ToolSpec Tool = new("get_overview", "Overview", "desc", new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() });

    private static ChatRequest Conversation() => new("model-x", "system text",
    [
        new UserMessage("how much?"),
        new AssistantMessage("checking", [new ToolCall("call-1", "get_overview", new JsonObject { ["a"] = 1 }), new ToolCall("call-2", "get_overview", [])]),
        new ToolResultMessage("call-1", "get_overview", "{\"rows\":[]}", false),
        new ToolResultMessage("call-2", "get_overview", "{\"error\":\"x\"}", true)
    ], [Tool], 512);

    #region 协议

    [Test]
    public void Anthropic_BuildsToolUseAndMergesToolResultsIntoOneUserTurn()
    {
        var body = AnthropicChatModel.BuildBody(Conversation());

        body["system"]!.GetValue<string>().Should().Be("system text");
        body["max_tokens"]!.GetValue<int>().Should().Be(512);
        var messages = body["messages"]!.AsArray();
        messages.Should().HaveCount(3);
        messages[1]!["content"]!.AsArray().Select(block => block!["type"]!.GetValue<string>()).Should().Equal("text", "tool_use", "tool_use");
        var results = messages[2]!["content"]!.AsArray();
        results.Should().HaveCount(2);
        results[1]!["is_error"]!.GetValue<bool>().Should().BeTrue();
        body["tools"]![0]!["input_schema"].Should().NotBeNull();
    }

    [Test]
    public void Anthropic_ParsesTextAndToolUse()
    {
        var response = AnthropicChatModel.ParseResponse(JsonNode.Parse("""
            {"content":[{"type":"text","text":"Let me look."},{"type":"tool_use","id":"tu_1","name":"get_top_games","input":{"limit":3}}],"stop_reason":"tool_use"}
            """)!);

        response.Text.Should().Be("Let me look.");
        response.ToolCalls.Should().ContainSingle().Which.Arguments["limit"]!.GetValue<int>().Should().Be(3);
    }

    [Test]
    public void OpenAi_BuildsFunctionCallsAndToolMessagesWithoutMaxTokens()
    {
        var body = OpenAiChatModel.BuildBody(Conversation());

        body.ContainsKey("max_tokens").Should().BeFalse();
        var messages = body["messages"]!.AsArray();
        messages[0]!["role"]!.GetValue<string>().Should().Be("system");
        messages[2]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>().Should().Be("{\"a\":1}");
        messages.Count(message => message!["role"]!.GetValue<string>() == "tool").Should().Be(2);
        body["tools"]![0]!["function"]!["parameters"].Should().NotBeNull();
    }

    [Test]
    public void OpenAi_ParsesStringArgumentsAndToleratesBrokenJson()
    {
        var response = OpenAiChatModel.ParseResponse(JsonNode.Parse("""
            {"choices":[{"message":{"content":null,"tool_calls":[
              {"id":"c1","type":"function","function":{"name":"find_games","arguments":"{\"query\":\"portal\"}"}},
              {"id":"c2","type":"function","function":{"name":"get_overview","arguments":"{not json"}}]}}]}
            """)!);

        response.Text.Should().BeEmpty();
        response.ToolCalls[0].Arguments["query"]!.GetValue<string>().Should().Be("portal");
        response.ToolCalls[1].Arguments.Count.Should().Be(0);
    }

    [Test]
    public void Anthropic_AcceptsBaseUrlWithOrWithoutVersion()
    {
        AnthropicChatModel.Root("https://api.anthropic.com/v1/").Should().Be("https://api.anthropic.com");
        AnthropicChatModel.Root("https://proxy.example/anthropic").Should().Be("https://proxy.example/anthropic");
    }

    [TestCase(401, ChatFailureKind.Unauthorized)]
    [TestCase(403, ChatFailureKind.Unauthorized)]
    [TestCase(429, ChatFailureKind.RateLimited)]
    [TestCase(503, ChatFailureKind.Unreachable)]
    [TestCase(404, ChatFailureKind.Invalid)]
    public void StatusCodes_MapToFailureKinds(int status, ChatFailureKind kind)
        => ChatModelException.KindOf(status).Should().Be(kind);

    [Test]
    public async Task HttpModel_RecordsTheExactBodyAndKeepsTheKeyOutOfIt()
    {
        string? recorded = null;
        var handler = new StubHandler("""{"content":[{"type":"text","text":"hi"}]}""");
        var model = new AnthropicChatModel(new HttpClient(handler), "https://api.anthropic.com", "sk-secret", body => recorded = body);

        var response = await model.CompleteAsync(new ChatRequest("m", "s", [new UserMessage("q")], []));

        response.Text.Should().Be("hi");
        recorded.Should().Be(handler.Body);
        recorded.Should().NotContain("sk-secret");
        handler.Headers.Should().Contain("x-api-key");
    }

    [Test]
    public async Task HttpModel_TurnsHttpErrorsIntoChatModelExceptions()
    {
        var model = new OpenAiChatModel(new HttpClient(new StubHandler("{}", System.Net.HttpStatusCode.Unauthorized)), "https://api.openai.com/v1", "k");

        var act = () => model.CompleteAsync(new ChatRequest("m", "s", [new UserMessage("q")], []));

        (await act.Should().ThrowAsync<ChatModelException>()).Which.Kind.Should().Be(ChatFailureKind.Unauthorized);
    }

    private sealed class StubHandler(string response, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public string Headers { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Headers = request.Headers.ToString();
            return new HttpResponseMessage(status) { Content = new StringContent(response) };
        }
    }

    #endregion

    #region 对话循环

    private sealed class ScriptedModel(params ChatResponse[] replies) : IChatModel
    {
        private int _index;
        public List<ChatRequest> Requests { get; } = [];

        public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(replies[Math.Min(_index++, replies.Length - 1)]);
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
    }

    [Test]
    public async Task AgentLoop_RunsToolsAndReturnsTheFinalAnswer()
    {
        var model = new ScriptedModel(
            new ChatResponse("", [new ToolCall("1", "get_overview", [])]),
            new ChatResponse("You played 3 hours.", []));
        var executed = new List<string>();

        var result = await AgentLoop.RunAsync(model, new ChatRequest("m", "s", [new UserMessage("q")], [Tool]),
            (call, _) => { executed.Add(call.Name); return Task.FromResult(new ToolOutcome("{}", false)); });

        result.Text.Should().Be("You played 3 hours.");
        result.Requests.Should().Be(2);
        executed.Should().Equal("get_overview");
        model.Requests[1].Messages.OfType<ToolResultMessage>().Should().ContainSingle();
    }

    [Test]
    public async Task AgentLoop_StopsAtTheStepLimitAndWithholdsToolsOnTheLastStep()
    {
        var model = new ScriptedModel(new ChatResponse("", [new ToolCall("1", "get_overview", [])]));

        var result = await AgentLoop.RunAsync(model, new ChatRequest("m", "s", [new UserMessage("q")], [Tool]),
            (_, _) => Task.FromResult(new ToolOutcome("{}", false)), maxSteps: 3);

        result.HitLimit.Should().BeTrue();
        model.Requests.Should().HaveCount(3);
        model.Requests[^1].Tools.Should().BeEmpty();
    }

    [Test]
    public async Task AgentLoop_ReportsToolFailuresToTheModelInsteadOfThrowing()
    {
        var model = new ScriptedModel(new ChatResponse("", [new ToolCall("1", "boom", [])]), new ChatResponse("done", []));

        var result = await AgentLoop.RunAsync(model, new ChatRequest("m", "s", [new UserMessage("q")], [Tool]),
            (_, _) => throw new InvalidOperationException("secret detail"));

        result.Text.Should().Be("done");
        var error = model.Requests[1].Messages.OfType<ToolResultMessage>().Single();
        error.IsError.Should().BeTrue();
        error.Content.Should().NotContain("secret detail");
    }

    #endregion

    #region 代号

    [Test]
    public void Placeholders_HideNamesUnlessAuthorized()
    {
        var names = new Placeholders(revealGames: false, revealFriends: false);
        var text = names.FromWeeklyText("[[g:570|Dota 2]] with [[f:76561198000000009|Pal]]");

        text.Should().Be("{G1} with {F1}");
        names.Legend(false).Should().NotContain("Dota").And.NotContain("Pal");
        names.ToWeeklyText("{G1} and {F1} and {G9}").Should().Be("[[g:570|Dota 2]] and [[f:76561198000000009|Pal]] and ");
        names.ToPlainText("{G1}!").Should().Be("Dota 2!");
    }

    [Test]
    public void Placeholders_RevealAuthorizedNamesButNeverHiddenGames()
    {
        var names = new Placeholders(revealGames: true, revealFriends: false, hiddenAppIds: new HashSet<int> { 10 });
        names.Game(570, "Dota 2");
        names.Game(10, "Secret Game");

        var legend = names.Legend(false);

        legend.Should().Contain("{G1} = Dota 2").And.NotContain("Secret Game");
    }

    [Test]
    public void ModelOutput_ExtractsJsonFromChattyReplies()
    {
        ModelOutput.ExtractJson("Sure!\n```json\n{\"zh\":\"a\",\"en\":\"b\"}\n```")!["en"]!.GetValue<string>().Should().Be("b");
        ModelOutput.ExtractJson("[{\"index\":0,\"title\":\"x\"}] trailing")!.AsArray().Should().HaveCount(1);
        ModelOutput.ExtractJson("no json").Should().BeNull();
        ModelOutput.CleanLine("「标题」", 10).Should().Be("标题");
    }

    #endregion

    #region MCP 与 Prometheus

    private sealed class FakeHost : IMcpToolHost
    {
        public IReadOnlyList<ToolSpec> Tools { get; } = [Tool];
        public Task<McpToolResult> CallAsync(string name, JsonObject arguments, CancellationToken cancellationToken)
            => Task.FromResult(new McpToolResult("{\"rows\":[[1]]}", false));
    }

    private static readonly McpServerInfo Info = new("steam-stat", "1.0", "read-only");

    [Test]
    public async Task Mcp_NegotiatesTheProtocolVersion()
    {
        var reply = JsonNode.Parse((await McpProtocol.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""", new FakeHost(), Info))!)!;
        reply["result"]!["protocolVersion"]!.GetValue<string>().Should().Be("2025-06-18");

        var unknown = JsonNode.Parse((await McpProtocol.HandleAsync("""{"jsonrpc":"2.0","id":2,"method":"initialize","params":{"protocolVersion":"1999-01-01"}}""", new FakeHost(), Info))!)!;
        unknown["result"]!["protocolVersion"]!.GetValue<string>().Should().Be(McpProtocol.LatestVersion);
        unknown["result"]!["capabilities"]!["tools"].Should().NotBeNull();
    }

    [Test]
    public async Task Mcp_ListsReadOnlyToolsAndCallsThem()
    {
        var list = JsonNode.Parse((await McpProtocol.HandleAsync("""{"jsonrpc":"2.0","id":"a","method":"tools/list"}""", new FakeHost(), Info))!)!;
        var tool = list["result"]!["tools"]![0]!;
        tool["name"]!.GetValue<string>().Should().Be("get_overview");
        tool["annotations"]!["readOnlyHint"]!.GetValue<bool>().Should().BeTrue();

        var call = JsonNode.Parse((await McpProtocol.HandleAsync("""{"jsonrpc":"2.0","id":"b","method":"tools/call","params":{"name":"get_overview","arguments":{}}}""", new FakeHost(), Info))!)!;
        call["result"]!["content"]![0]!["text"]!.GetValue<string>().Should().Contain("rows");
        call["result"]!["isError"]!.GetValue<bool>().Should().BeFalse();
    }

    [TestCase("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", null)]
    [TestCase("""{"jsonrpc":"2.0","id":3,"method":"resources/list"}""", McpProtocol.MethodNotFound)]
    [TestCase("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"drop_tables"}}""", McpProtocol.InvalidParams)]
    [TestCase("""{"jsonrpc":"1.0","id":5,"method":"ping"}""", McpProtocol.InvalidRequest)]
    [TestCase("not json", McpProtocol.ParseError)]
    public async Task Mcp_RejectsWhatItDoesNotSupport(string message, int? code)
    {
        var reply = await McpProtocol.HandleAsync(message, new FakeHost(), Info);
        if (code == null) reply.Should().BeNull();
        else JsonNode.Parse(reply!)!["error"]!["code"]!.GetValue<int>().Should().Be(code.Value);
    }

    [Test]
    public void Prometheus_EscapesLabelValues()
    {
        var text = PrometheusText.Write([
            new PrometheusMetric("steamstat_game_playtime_seconds", "gauge", "help\nline", [new([new("game", "A \"quoted\" \\ name")], 3600)])
        ]);

        text.Should().Contain("# HELP steamstat_game_playtime_seconds help\\nline\n");
        text.Should().Contain("steamstat_game_playtime_seconds{game=\"A \\\"quoted\\\" \\\\ name\"} 3600\n");
    }

    #endregion

    #region 设置

    [Test]
    public void ExternalScopes_AlwaysIncludeAggregateAndDropFriendsInStreamerMode()
    {
        ExternalScopes.Normalize(["friends", "bogus", "games"]).Should().Equal("aggregate", "games", "friends");
        ExternalScopes.Effective(["friends", "games"], streamerMode: true).Should().BeEquivalentTo(["aggregate", "games"]);
    }

    #endregion
}
