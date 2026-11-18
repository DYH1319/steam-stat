using System.Text.Json;
using System.Text.Json.Nodes;

namespace SteamStat.Core.Assistant;

public sealed record McpToolResult(string Text, bool IsError, JsonObject? Structured = null);

/// <summary>MCP 服务器背后的工具。实现方负责授权和打码，协议层只管 JSON-RPC。</summary>
public interface IMcpToolHost
{
    IReadOnlyList<ToolSpec> Tools { get; }
    Task<McpToolResult> CallAsync(string name, JsonObject arguments, CancellationToken cancellationToken);
}

public sealed record McpServerInfo(string Name, string Version, string Instructions);

/// <summary>
/// Model Context Protocol 的服务端（只有 tools，全部只读）。传输无关：
/// Streamable HTTP 由本地接口的 /mcp 承载，stdio 由命令行工具 <c>steamstat mcp</c> 转发到 /mcp。
/// </summary>
public static class McpProtocol
{
    public const string LatestVersion = "2025-11-25";
    public static readonly IReadOnlyList<string> SupportedVersions = [LatestVersion, "2025-06-18", "2025-03-26", "2024-11-05"];

    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;

    /// <summary>处理一条消息（或旧版本的批量数组）。全是通知时返回 null，调用方回 202。</summary>
    public static async Task<string?> HandleAsync(string body, IMcpToolHost host, McpServerInfo info, CancellationToken cancellationToken = default)
    {
        JsonNode? message;
        try
        {
            message = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return Error(null, ParseError, "Parse error").ToJsonString();
        }

        if (message is JsonArray batch)
        {
            var responses = new JsonArray();
            foreach (var item in batch)
                if (await HandleOneAsync(item, host, info, cancellationToken).ConfigureAwait(false) is { } response) responses.Add(response);
            return responses.Count == 0 ? null : responses.ToJsonString();
        }
        return (await HandleOneAsync(message, host, info, cancellationToken).ConfigureAwait(false))?.ToJsonString();
    }

    private static async Task<JsonObject?> HandleOneAsync(JsonNode? node, IMcpToolHost host, McpServerInfo info, CancellationToken cancellationToken)
    {
        if (node is not JsonObject message || Str(message["jsonrpc"]) != "2.0")
            return Error(null, InvalidRequest, "Invalid request");
        var id = message["id"]?.DeepClone();
        var method = Str(message["method"]);
        // 客户端发来的响应（我们不发请求）和通知都不回复
        if (method == null) return id == null ? null : Error(id, InvalidRequest, "Invalid request");
        if (id == null) return null;
        var parameters = message["params"] as JsonObject ?? [];

        switch (method)
        {
            case "initialize":
                var requested = Str(parameters["protocolVersion"]);
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = requested != null && SupportedVersions.Contains(requested) ? requested : LatestVersion,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = info.Name, ["title"] = "Steam Stat", ["version"] = info.Version },
                    ["instructions"] = info.Instructions
                });
            case "ping":
                return Result(id, []);
            case "tools/list":
                return Result(id, new JsonObject
                {
                    ["tools"] = new JsonArray(host.Tools.Select(tool => (JsonNode)new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["title"] = tool.Title,
                        ["description"] = tool.Description,
                        ["inputSchema"] = tool.InputSchema.DeepClone(),
                        ["annotations"] = new JsonObject
                        {
                            ["title"] = tool.Title,
                            ["readOnlyHint"] = true,
                            ["destructiveHint"] = false,
                            ["idempotentHint"] = true,
                            ["openWorldHint"] = false
                        }
                    }).ToArray())
                });
            case "tools/call":
                var name = Str(parameters["name"]);
                if (name == null || host.Tools.All(tool => tool.Name != name))
                    return Error(id, InvalidParams, $"Unknown tool: {name}");
                var result = await host.CallAsync(name, parameters["arguments"] as JsonObject ?? [], cancellationToken).ConfigureAwait(false);
                var payload = new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = result.Text }),
                    ["isError"] = result.IsError
                };
                if (result.Structured != null) payload["structuredContent"] = result.Structured.DeepClone();
                return Result(id, payload);
            default:
                return Error(id, MethodNotFound, $"Method not found: {method}");
        }
    }

    private static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static JsonObject Result(JsonNode id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    };
}
