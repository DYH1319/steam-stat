using System.Text.Json.Nodes;

namespace SteamStat.Core.Assistant;

/// <summary>一个只读工具的说明。AI 的「问档案」和 MCP 用同一份定义。</summary>
public sealed record ToolSpec(string Name, string Title, string Description, JsonObject InputSchema);

public sealed record ToolCall(string Id, string Name, JsonObject Arguments);

public abstract record ChatMessage;

public sealed record UserMessage(string Text) : ChatMessage;

public sealed record AssistantMessage(string Text, IReadOnlyList<ToolCall> ToolCalls) : ChatMessage;

public sealed record ToolResultMessage(string ToolCallId, string Name, string Content, bool IsError) : ChatMessage;

public sealed record ChatRequest(
    string Model,
    string System,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<ToolSpec> Tools,
    int MaxTokens = 2048);

public sealed record ChatResponse(string Text, IReadOnlyList<ToolCall> ToolCalls);

public enum ChatFailureKind
{
    /// <summary>Key 错误或没有权限。</summary>
    Unauthorized,
    /// <summary>连不上、超时、服务端 5xx。</summary>
    Unreachable,
    /// <summary>请求被拒绝（模型不存在、参数错误）或回复读不懂。</summary>
    Invalid,
    RateLimited
}

/// <summary>模型服务的失败。属于外部服务不可用，不是缺陷，不进崩溃报告。</summary>
public sealed class ChatModelException(ChatFailureKind kind, string message, int? statusCode = null, Exception? innerException = null)
    : Exception(message, innerException)
{
    public ChatFailureKind Kind { get; } = kind;
    public int? StatusCode { get; } = statusCode;

    public static ChatFailureKind KindOf(int statusCode) => statusCode switch
    {
        401 or 403 => ChatFailureKind.Unauthorized,
        429 => ChatFailureKind.RateLimited,
        >= 500 => ChatFailureKind.Unreachable,
        _ => ChatFailureKind.Invalid
    };
}

public interface IChatModel
{
    Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 两家协议共用的 HTTP 部分：每个请求体发出之前先交给 <c>onRequest</c>（审计日志），
/// 失败统一换成 <see cref="ChatModelException"/>。Key 只放在请求头里，不进审计。
/// </summary>
public abstract class HttpChatModel(HttpClient http, Action<string>? onRequest) : IChatModel
{
    public abstract Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default);
    public abstract Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default);

    protected abstract void Authorize(HttpRequestMessage request);

    protected async Task<JsonNode> SendAsync(HttpMethod method, Uri uri, JsonNode? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        if (body != null)
        {
            var json = body.ToJsonString();
            onRequest?.Invoke(json);
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }
        Authorize(request);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new ChatModelException(ChatFailureKind.Unreachable, exception.GetType().Name, null, exception);
        }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new ChatModelException(ChatModelException.KindOf((int)response.StatusCode), $"HTTP {(int)response.StatusCode}", (int)response.StatusCode);
            try
            {
                return JsonNode.Parse(text) ?? throw new ChatModelException(ChatFailureKind.Invalid, "Empty response");
            }
            catch (System.Text.Json.JsonException exception)
            {
                throw new ChatModelException(ChatFailureKind.Invalid, "Response is not JSON", (int)response.StatusCode, exception);
            }
        }
    }

    internal static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    internal static Uri Combine(string baseUrl, string path) => new(baseUrl.TrimEnd('/') + path);

    /// <summary>模型可能把参数写成不合法的 JSON，这时当成空参数，让工具自己报缺参数。</summary>
    internal static JsonObject ParseArguments(JsonNode? node)
    {
        if (node is JsonObject obj) return (JsonObject)obj.DeepClone();
        if (node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
        {
            try
            {
                return JsonNode.Parse(text) as JsonObject ?? [];
            }
            catch (System.Text.Json.JsonException)
            {
                return [];
            }
        }
        return [];
    }
}
