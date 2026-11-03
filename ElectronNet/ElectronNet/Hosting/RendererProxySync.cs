using ElectronNET.API.Entities;
using ElectronNet.Infrastructure;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Http;

namespace ElectronNet.Hosting;

/// <summary>
/// 把网络设置中的代理同步到 Chromium 默认会话：渲染层直接加载的 Steam 图片 / 头像、
/// 以及 electron-updater（经 Electron net 模块）都走这里。环回地址始终直连（Vite 开发服务器）。
/// </summary>
internal sealed class RendererProxySync(
    MainWindowAccessor mainWindowAccessor,
    ISteamNetworkController networkController,
    ILogger<RendererProxySync> logger)
{
    private const string BypassRules = "<local>;localhost;127.0.0.1;[::1]";
    private int _attached;

    internal async Task AttachAsync()
    {
        if (Interlocked.Exchange(ref _attached, 1) != 0) return;
        networkController.Changed += profile => _ = ApplyAsync(profile);
        // 默认即跟随系统代理，启动时无需额外设置
        if (networkController.Current.ProxyMode != SteamProxyMode.System)
            await ApplyAsync(networkController.Current);
    }

    internal static ProxyConfig ToProxyConfig(SteamNetworkProfile profile) => profile switch
    {
        { ProxyMode: SteamProxyMode.Custom, ProxyUri: { } uri } => new ProxyConfig(null!, $"{uri.Scheme}://{uri.Authority}", BypassRules)
        {
            Mode = "fixed_servers"
        },
        { ProxyMode: SteamProxyMode.None } => new ProxyConfig(null!, null!, null!) { Mode = "direct" },
        _ => new ProxyConfig(null!, null!, null!) { Mode = "system" }
    };

    private async Task ApplyAsync(SteamNetworkProfile profile)
    {
        var window = mainWindowAccessor.Window;
        if (window == null) return;
        try
        {
            await window.WebContents.Session.SetProxyAsync(ToProxyConfig(profile)).WaitAsync(TimeSpan.FromSeconds(5));
            logger.LogInformation("Applied renderer proxy mode {ProxyMode}", SteamNetworkProfile.FormatMode(profile.ProxyMode));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to apply renderer proxy settings");
        }
    }
}
