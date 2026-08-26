using System.Net;

namespace ElectronNet.Helpers;

/// <summary>
/// 共享的 HttpClient 实例。
///
/// 每次请求都 <c>new HttpClient()</c> 会让底层连接堆积在 TIME_WAIT 状态，
/// 高频调用（例如批量下载头像）时会耗尽本机端口。因此这里按用途维护少量长生命周期实例。
///
/// 使用 <see cref="SocketsHttpHandler.PooledConnectionLifetime"/> 定期回收连接，
/// 避免长生命周期 HttpClient 感知不到 DNS 变化——这是长驻单例 HttpClient 的经典陷阱。
///
/// TODO(Phase 1): 引入 DI 后替换为 IHttpClientFactory，并在此处挂上限流 / 重试策略。
/// </summary>
public static class HttpClientProvider
{
    /// <summary>下载图片等静态资源（头像、头像框、动态头像）</summary>
    public static HttpClient Download { get; } = Create(TimeSpan.FromSeconds(30));

    /// <summary>调用 Steam Web API / Store API</summary>
    public static HttpClient SteamApi { get; } = Create(TimeSpan.FromSeconds(15));

    private static HttpClient Create(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All
        };
        return new HttpClient(handler) { Timeout = timeout };
    }
}
