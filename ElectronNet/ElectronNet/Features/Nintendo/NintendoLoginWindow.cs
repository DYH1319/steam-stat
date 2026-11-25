using ElectronNET.API;
using ElectronNET.API.Entities;
using ElectronNet.Hosting;
using ElectronNet.Infrastructure;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Nintendo;

namespace ElectronNet.Features.Nintendo;

/// <summary>应用内的任天堂登录窗口。测试里换成假的。</summary>
internal interface INintendoLoginWindow
{
    /// <summary>打开登录页。截获到回调时调用 onCallback，用户关掉窗口时调用 onClosed。返回是否打开成功。</summary>
    Task<bool> OpenAsync(Uri url, Action<string> onCallback, Action onClosed, CancellationToken cancellationToken = default);

    void Close();
}

/// <summary>
/// 登录页跑在单独的内存会话分区里（不带应用的 Cookie，关掉应用就清空），不加载 preload、沙箱化。
/// 「选择此人」是 npf…://auth 的深链：由 Electron.NET fork 的 InterceptNavigation 在跳转前拦下并交回 Host，
/// 不会交给操作系统（否则系统会弹出「没有应用能打开这个链接」）。
/// </summary>
internal sealed class ElectronNintendoLoginWindow(IMainWindowAccessor mainWindow, ILogger<ElectronNintendoLoginWindow> logger) : INintendoLoginWindow
{
    private const string Partition = "steam-stat-nintendo-login";
    /// <summary>任天堂的登录页是给手机 App 内嵌的，用手机浏览器的标识，页面排版才对。</summary>
    private const string MobileUserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1";

    private readonly Lock _lock = new();
    private BrowserWindow? _window;

    public async Task<bool> OpenAsync(Uri url, Action<string> onCallback, Action onClosed, CancellationToken cancellationToken = default)
    {
        Close();
        try
        {
            var window = await Electron.WindowManager.CreateWindowAsync(
                new BrowserWindowOptions
                {
                    Width = 460,
                    Height = 720,
                    MinWidth = 380,
                    MinHeight = 560,
                    Show = false,
                    Center = true,
                    AutoHideMenuBar = true,
                    Icon = PlatformRuntime.IconPath(),
                    Title = "Nintendo Account",
                    WebPreferences = new WebPreferences
                    {
                        Partition = Partition,
                        DevTools = false,
                        WebSecurity = true,
                        AllowRunningInsecureContent = false,
                        ContextIsolation = true,
                        NodeIntegration = false,
                        NodeIntegrationInWorker = false,
                        NodeIntegrationInSubFrames = false,
                        Sandbox = true
                    }
                },
                "about:blank").ConfigureAwait(false);
            lock (_lock) _window = window;

            var finished = 0;
            window.WebContents.OnNavigationIntercepted += callback =>
            {
                if (Interlocked.Exchange(ref finished, 1) != 0) return;
                onCallback(callback);
                Close();
            };
            window.OnClosed += () =>
            {
                lock (_lock)
                {
                    if (ReferenceEquals(_window, window)) _window = null;
                }
                if (Interlocked.Exchange(ref finished, 1) == 0) onClosed();
            };
            window.OnReadyToShow += () => window.Show();
            window.WebContents.InterceptNavigation(NintendoEndpoints.RedirectScheme + ":");

            var snapshot = await mainWindow.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot.Window != null) window.SetParentWindow(snapshot.Window);
            await window.WebContents.LoadURLAsync(url.AbsoluteUri, new LoadURLOptions { UserAgent = MobileUserAgent }).ConfigureAwait(false);
            window.Show();
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Failed to open the Nintendo login window");
            Close();
            return false;
        }
    }

    public void Close()
    {
        BrowserWindow? window;
        lock (_lock)
        {
            window = _window;
            _window = null;
        }
        try
        {
            window?.Close();
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "The Nintendo login window was already closing");
        }
    }
}
