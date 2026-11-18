namespace SteamStat.Core.Assistant;

public sealed record ToolOutcome(string Content, bool IsError);

public sealed record AgentProgress(int Step, string? Tool);

public sealed record AgentResult(string Text, int Requests, bool HitLimit);

/// <summary>
/// 问档案的对话循环：模型要调工具就执行、把结果交回去，直到它给出回答或者步数用完。
/// 工具只能读档案，循环本身不碰数据。
/// </summary>
public static class AgentLoop
{
    public const int DefaultMaxSteps = 8;

    public static async Task<AgentResult> RunAsync(
        IChatModel model,
        ChatRequest request,
        Func<ToolCall, CancellationToken, Task<ToolOutcome>> execute,
        IProgress<AgentProgress>? progress = null,
        int maxSteps = DefaultMaxSteps,
        CancellationToken cancellationToken = default)
    {
        var messages = request.Messages.ToList();
        var requests = 0;
        for (var step = 0; step < maxSteps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new AgentProgress(step, null));
            // 最后一步不再给工具，逼模型用已有结果作答
            var tools = step == maxSteps - 1 ? [] : request.Tools;
            var response = await model.CompleteAsync(request with { Messages = messages, Tools = tools }, cancellationToken).ConfigureAwait(false);
            requests++;
            if (response.ToolCalls.Count == 0)
                return new AgentResult(response.Text.Trim(), requests, false);

            messages.Add(new AssistantMessage(response.Text, response.ToolCalls));
            foreach (var call in response.ToolCalls)
            {
                progress?.Report(new AgentProgress(step, call.Name));
                ToolOutcome outcome;
                try
                {
                    outcome = await execute(call, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    outcome = new ToolOutcome($$"""{"error":"tool failed: {{exception.GetType().Name}}"}""", true);
                }
                messages.Add(new ToolResultMessage(call.Id, call.Name, outcome.Content, outcome.IsError));
            }
        }
        return new AgentResult(string.Empty, requests, true);
    }
}
