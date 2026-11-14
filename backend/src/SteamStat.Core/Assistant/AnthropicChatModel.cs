using System.Text.Json.Nodes;

namespace SteamStat.Core.Assistant;

/// <summary>Anthropic Messages API（/v1/messages），工具用 tool_use / tool_result 块。</summary>
public sealed class AnthropicChatModel(HttpClient http, string baseUrl, string apiKey, Action<string>? onRequest = null)
    : HttpChatModel(http, onRequest)
{
    internal const string ApiVersion = "2023-06-01";

    /// <summary>用户可能填 https://api.anthropic.com 或带 /v1 的地址，两种都接受。</summary>
    internal static string Root(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        return trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? trimmed[..^3] : trimmed;
    }

    public override async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
        => ParseResponse(await SendAsync(HttpMethod.Post, Combine(Root(baseUrl), "/v1/messages"), BuildBody(request), cancellationToken).ConfigureAwait(false));

    public override async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var node = await SendAsync(HttpMethod.Get, Combine(Root(baseUrl), "/v1/models?limit=100"), null, cancellationToken).ConfigureAwait(false);
        return (node["data"] as JsonArray ?? [])
            .Select(item => Str(item?["id"]))
            .OfType<string>()
            .ToList();
    }

    protected override void Authorize(HttpRequestMessage request)
    {
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", ApiVersion);
    }

    internal static JsonObject BuildBody(ChatRequest request)
    {
        var messages = new JsonArray();
        foreach (var message in request.Messages)
        {
            switch (message)
            {
                case UserMessage user:
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = user.Text });
                    break;
                case AssistantMessage assistant:
                    var blocks = new JsonArray();
                    if (assistant.Text.Length > 0) blocks.Add(new JsonObject { ["type"] = "text", ["text"] = assistant.Text });
                    foreach (var call in assistant.ToolCalls)
                        blocks.Add(new JsonObject { ["type"] = "tool_use", ["id"] = call.Id, ["name"] = call.Name, ["input"] = call.Arguments.DeepClone() });
                    messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = blocks });
                    break;
                case ToolResultMessage result:
                    // 同一轮的多个工具结果要合并进同一条 user 消息
                    var block = new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = result.ToolCallId, ["content"] = result.Content };
                    if (result.IsError) block["is_error"] = true;
                    if (messages.Count > 0 && messages[^1] is JsonObject { } last && Str(last["role"]) == "user" && last["content"] is JsonArray content)
                        content.Add(block);
                    else
                        messages.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(block) });
                    break;
            }
        }

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["max_tokens"] = request.MaxTokens,
            ["system"] = request.System,
            ["messages"] = messages
        };
        if (request.Tools.Count > 0)
            body["tools"] = new JsonArray(request.Tools.Select(tool => (JsonNode)new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["input_schema"] = tool.InputSchema.DeepClone()
            }).ToArray());
        return body;
    }

    internal static ChatResponse ParseResponse(JsonNode node)
    {
        if (node["content"] is not JsonArray content)
            throw new ChatModelException(ChatFailureKind.Invalid, "Missing content");
        var text = new System.Text.StringBuilder();
        var calls = new List<ToolCall>();
        foreach (var block in content.OfType<JsonObject>())
        {
            switch (Str(block["type"]))
            {
                case "text":
                    text.Append(Str(block["text"]));
                    break;
                case "tool_use":
                    calls.Add(new ToolCall(
                        Str(block["id"]) ?? $"call-{calls.Count}",
                        Str(block["name"]) ?? string.Empty,
                        ParseArguments(block["input"])));
                    break;
            }
        }
        return new ChatResponse(text.ToString(), calls);
    }
}
