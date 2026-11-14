using System.Text.Json.Nodes;

namespace SteamStat.Core.Assistant;

/// <summary>
/// OpenAI 兼容的 Chat Completions（/chat/completions）。OpenAI、各家兼容服务和本地 Ollama（/v1）都走这里，
/// Ollama 不需要 Key 时不带 Authorization 头。
/// </summary>
public sealed class OpenAiChatModel(HttpClient http, string baseUrl, string? apiKey, Action<string>? onRequest = null)
    : HttpChatModel(http, onRequest)
{
    public override async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
        => ParseResponse(await SendAsync(HttpMethod.Post, Combine(baseUrl, "/chat/completions"), BuildBody(request), cancellationToken).ConfigureAwait(false));

    public override async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var node = await SendAsync(HttpMethod.Get, Combine(baseUrl, "/models"), null, cancellationToken).ConfigureAwait(false);
        return (node["data"] as JsonArray ?? [])
            .Select(item => Str(item?["id"]))
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    protected override void Authorize(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(apiKey)) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
    }

    internal static JsonObject BuildBody(ChatRequest request)
    {
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = request.System } };
        foreach (var message in request.Messages)
        {
            switch (message)
            {
                case UserMessage user:
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = user.Text });
                    break;
                case AssistantMessage assistant:
                    var item = new JsonObject { ["role"] = "assistant", ["content"] = assistant.Text };
                    if (assistant.ToolCalls.Count > 0)
                        item["tool_calls"] = new JsonArray(assistant.ToolCalls.Select(call => (JsonNode)new JsonObject
                        {
                            ["id"] = call.Id,
                            ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Arguments.ToJsonString() }
                        }).ToArray());
                    messages.Add(item);
                    break;
                case ToolResultMessage result:
                    messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = result.ToolCallId, ["content"] = result.Content });
                    break;
            }
        }

        // 不带 max_tokens：OpenAI 的推理模型只认 max_completion_tokens，兼容服务又大多只认前者，用服务端默认值最稳
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = messages
        };
        if (request.Tools.Count > 0)
            body["tools"] = new JsonArray(request.Tools.Select(tool => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = tool.InputSchema.DeepClone()
                }
            }).ToArray());
        return body;
    }

    internal static ChatResponse ParseResponse(JsonNode node)
    {
        var message = node["choices"]?[0]?["message"] as JsonObject
                      ?? throw new ChatModelException(ChatFailureKind.Invalid, "Missing choices");
        var text = message["content"] is JsonValue content && content.TryGetValue<string>(out var value) ? value : string.Empty;
        var calls = new List<ToolCall>();
        foreach (var call in (message["tool_calls"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var function = call["function"] as JsonObject;
            calls.Add(new ToolCall(
                Str(call["id"]) ?? $"call-{calls.Count}",
                Str(function?["name"]) ?? string.Empty,
                ParseArguments(function?["arguments"])));
        }
        return new ChatResponse(text, calls);
    }
}
