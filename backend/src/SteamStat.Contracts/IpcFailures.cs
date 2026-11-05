namespace SteamStat.Contracts.Ipc;

/// <summary>
/// Host 处理器失败时 invoke 的拒绝原因。消息只有前缀和错误码：异常文本可能含本地路径，不发给渲染层。
/// 渲染层的对应实现在 UI 仓 <c>utils/ipcFailure.ts</c>。
/// </summary>
public static class IpcFailureCodes
{
    public const string MessagePrefix = "steam-stat-ipc-failure:";

    /// <summary>操作被取消，不算失败。</summary>
    public const string Cancelled = "cancelled";

    /// <summary>网络、Steam 或同步存储等外部服务暂时不可用，不是缺陷。</summary>
    public const string Unavailable = "unavailable";

    /// <summary>请求没有通过契约校验，说明渲染层和 Host 不一致，是缺陷。</summary>
    public const string InvalidRequest = "invalid-request";

    /// <summary>其余所有异常，按缺陷处理。</summary>
    public const string Internal = "internal";

    public static readonly IReadOnlyList<string> All = [Cancelled, Unavailable, InvalidRequest, Internal];
}
