using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using SteamKit2;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Http;
using SteamStat.Core.Steam.Gateway;
using SteamStat.Core.Sync;

namespace ElectronNet.Infrastructure;

/// <summary>把 IPC 处理器的失败上报给崩溃报告。</summary>
internal interface IIpcFailureRecorder
{
    void RecordIpcFailure(string endpoint, Exception exception);
}

/// <summary>
/// 决定 IPC 处理器失败后怎么办。这些异常被接住后不再是未观察异常，这里是它们进入崩溃报告的唯一入口。
/// 只有明确属于外部环境或取消的才不上报，判断不出来的一律按缺陷上报。
/// </summary>
internal sealed class IpcFailurePolicy(IIpcFailureRecorder recorder, ILogger<IpcFailurePolicy> logger)
{
    private readonly ConcurrentDictionary<(string Endpoint, string Type), byte> _seen = new();

    internal object Run(IIpcEndpointDescriptor endpoint, Func<object> handler)
    {
        try
        {
            return handler();
        }
        catch (Exception exception)
        {
            throw Translate(endpoint, exception);
        }
    }

    internal async Task<object> RunAsync(IIpcEndpointDescriptor endpoint, Func<Task<object>> handler)
    {
        try
        {
            return await handler().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw Translate(endpoint, exception);
        }
    }

    internal static string Classify(Exception exception)
    {
        // 先看整条链：HttpClient 超时是带 TimeoutException 的 TaskCanceledException，不是取消
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (IsExternal(current)) return IpcFailureCodes.Unavailable;
        }
        return exception switch
        {
            OperationCanceledException => IpcFailureCodes.Cancelled,
            IpcRequestBindingException => IpcFailureCodes.InvalidRequest,
            _ => IpcFailureCodes.Internal
        };
    }

    private static bool IsExternal(Exception exception)
        => exception is HttpRequestException or TimeoutException or SocketException or WebSocketException
               or SyncStorageException or SteamRateLimiterRejectedException or SteamCmSchedulerRejectedException or AsyncJobFailedException
           || exception.GetType().Namespace?.StartsWith("Polly", StringComparison.Ordinal) == true;

    private IpcFailureException Translate(IIpcEndpointDescriptor endpoint, Exception exception)
    {
        var code = Classify(exception);
        // 轮询接口持续失败时每几秒就到这里一次，只记第一次，否则会占满崩溃报告的进程额度，挤掉别的缺陷
        var first = _seen.TryAdd((endpoint.ApiMethod, exception.GetType().FullName ?? exception.GetType().Name), 0);
        switch (code)
        {
            case IpcFailureCodes.Cancelled:
                logger.LogDebug("{Endpoint} was cancelled", endpoint.ApiMethod);
                break;
            case IpcFailureCodes.Unavailable when first:
                logger.LogWarning("{Endpoint} failed because an external service is unavailable: {Error}", endpoint.ApiMethod, exception.Message);
                break;
            case IpcFailureCodes.Unavailable:
                logger.LogDebug("{Endpoint} is still failing: {Error}", endpoint.ApiMethod, exception.Message);
                break;
            default:
                if (first)
                {
                    // 绑定失败已由 IpcRequestBinder 记录
                    if (code == IpcFailureCodes.Internal) logger.LogError(exception, "{Endpoint} failed", endpoint.ApiMethod);
                    recorder.RecordIpcFailure(endpoint.ApiMethod, exception);
                }
                else
                {
                    logger.LogDebug("{Endpoint} is still failing: {Error}", endpoint.ApiMethod, exception.Message);
                }
                break;
        }
        return new IpcFailureException(code);
    }
}

/// <summary>消息会原样到达渲染层，所以只含错误码。</summary>
internal sealed class IpcFailureException(string code) : Exception(IpcFailureCodes.MessagePrefix + code);
