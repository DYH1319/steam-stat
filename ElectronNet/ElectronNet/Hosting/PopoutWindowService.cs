using ElectronNET.API;
using ElectronNET.API.Entities;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Settings;

namespace ElectronNet.Hosting;

internal interface IPopoutWindows
{
    IReadOnlyList<BrowserWindow> Windows();
}

/// <summary>
/// 游戏详情、好友档案、报告的独立窗口。与主窗口同一个渲染层入口，靠地址里的 popout 参数切换成精简外壳。
/// 路由由 Host 校验，渲染层不能让窗口打开任意地址。
/// </summary>
internal sealed class PopoutWindowService(
    ISettingsStore settingsStore,
    WindowStateStore windowState,
    ILogger<PopoutWindowService> logger) : IPopoutWindows
{
    private const int MaxWindows = 8;
    private const int MinWidth = 720;
    private const int MinHeight = 480;
    private const int CascadeOffset = 32;

    private static readonly Dictionary<string, (int Width, int Height)> DefaultSizes = new()
    {
        ["game"] = (1180, 820),
        ["friend"] = (1100, 800),
        ["report"] = (1180, 800)
    };

    private sealed record Entry(string Id, PopoutRoute Route, BrowserWindow Window, WindowStateTracker Tracker);

    private readonly Lock _sync = new();
    private readonly List<Entry> _entries = [];
    private readonly SemaphoreSlim _openGate = new(1, 1);
    private int _nextId;

    private Entry[] Entries()
    {
        lock (_sync) return [.. _entries];
    }

    public async Task<PopoutOpenResultDto> OpenAsync(string path)
    {
        var route = PopoutRoutes.Parse(path);
        if (route == null)
        {
            logger.LogWarning("Rejected a popout window for a route that is not allowed");
            return new PopoutOpenResultDto { Opened = false, Existing = false, Error = "invalid" };
        }

        await _openGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var entries = Entries();
            if (entries.FirstOrDefault(entry => entry.Route.Key == route.Key) is { } existing)
            {
                await BringToFrontAsync(existing.Window).ConfigureAwait(false);
                return new PopoutOpenResultDto { Opened = true, Existing = true };
            }
            if (entries.Length >= MaxWindows)
            {
                return new PopoutOpenResultDto { Opened = false, Existing = false, Error = "limit" };
            }

            await CreateAsync(route, entries.Count(entry => entry.Route.Kind == route.Kind)).ConfigureAwait(false);
            return new PopoutOpenResultDto { Opened = true, Existing = false };
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to open a popout window");
            return new PopoutOpenResultDto { Opened = false, Existing = false, Error = "failed" };
        }
        finally
        {
            _openGate.Release();
        }
    }

    private static async Task BringToFrontAsync(BrowserWindow window)
    {
        if (await window.IsMinimizedAsync().ConfigureAwait(false)) window.Restore();
        window.Show();
        window.Focus();
    }

    private async Task CreateAsync(PopoutRoute route, int sameKindCount)
    {
        var screen = Electron.Screen;
        var nearest = await screen.GetDisplayNearestPointAsync(await screen.GetCursorScreenPointAsync().ConfigureAwait(false)).ConfigureAwait(false);
        var workAreas = (await screen.GetAllDisplaysAsync().ConfigureAwait(false)).Select(display => display.WorkArea).ToArray();

        windowState.Load().Popouts.TryGetValue(route.Kind, out var saved);
        // 同类窗口叠在同一位置时只能看到最上面一个，错开一点
        var cascade = sameKindCount * CascadeOffset;
        var restored = saved == null
            ? null
            : WindowBoundsRules.Fit(saved with { X = saved.X + cascade, Y = saved.Y + cascade }, workAreas, MinWidth, MinHeight);
        var defaultSize = DefaultSizes[route.Kind];
        var (width, height) = WindowBoundsRules.FitSize(defaultSize.Width, defaultSize.Height, nearest.WorkArea, MinWidth, MinHeight);

        var settings = settingsStore.GetSettings();
        var zoomFactor = settings.ZoomFactor!.Value;
        var material = WindowMaterials.Effective(settings.WindowMaterial);
        var id = $"popout-{Interlocked.Increment(ref _nextId)}";

        var window = await Electron.WindowManager.CreateWindowAsync(
            new BrowserWindowOptions
            {
                Width = restored?.Width ?? width,
                Height = restored?.Height ?? height,
                X = restored?.X,
                Y = restored?.Y,
                MinWidth = MinWidth,
                MinHeight = MinHeight,
                RoundedCorners = true,
                Icon = PlatformRuntime.IconPath(),
                Show = false,
                Center = restored == null,
                AutoHideMenuBar = true,
                Frame = false,
                TitleBarStyle = TitleBarStyle.hidden,
                BackgroundMaterial = material == WindowMaterials.None ? null : material,
                BackgroundColor = material == WindowMaterials.None ? null : WindowMaterials.TransparentColor,
                WebPreferences = Program.RendererWebPreferences(zoomFactor)
            },
            Program.PopoutUrl(id, route.Path)).ConfigureAwait(false);

        var tracker = new WindowStateTracker(window, restored, state => windowState.SavePopout(route.Kind, state), logger);
        var entry = new Entry(id, route, window, tracker);
        lock (_sync) _entries.Add(entry);

        window.OnReadyToShow += () =>
        {
            if (saved?.Maximized == true) window.Maximize();
            window.Show();
        };
        window.WebContents.OnDidFinishLoad += () =>
            window.WebContents.SetZoomFactor(settingsStore.GetSettings().ZoomFactor!.Value);
        window.OnClosed += () =>
        {
            tracker.Dispose();
            lock (_sync) _entries.Remove(entry);
        };
    }

    public async Task<PopoutStateDto> ControlAsync(string id, string action)
    {
        var window = Entries().FirstOrDefault(entry => entry.Id == id)?.Window;
        if (window == null) return new PopoutStateDto { Maximized = false };
        try
        {
            var maximized = await window.IsMaximizedAsync().ConfigureAwait(false);
            switch (action)
            {
                case "minimize":
                    window.Minimize();
                    break;
                case "maximize":
                    if (maximized) window.Unmaximize();
                    else window.Maximize();
                    maximized = !maximized;
                    break;
                case "close":
                    window.Close();
                    break;
            }
            return new PopoutStateDto { Maximized = maximized };
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to control a popout window");
            return new PopoutStateDto { Maximized = false };
        }
    }

    public IReadOnlyList<BrowserWindow> Windows() => [.. Entries().Select(entry => entry.Window)];

    public void SetZoomFactor(double zoomFactor) => ForEach(window => window.WebContents.SetZoomFactor(zoomFactor));

    public void SetWindowMaterial(string material) => ForEach(window => WindowMaterials.Apply(window, material));

    public void CloseAll() => ForEach(window => window.Close());

    private void ForEach(Action<BrowserWindow> action)
    {
        foreach (var entry in Entries())
        {
            try
            {
                action(entry.Window);
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Skipped a popout window that is closing");
            }
        }
    }
}
