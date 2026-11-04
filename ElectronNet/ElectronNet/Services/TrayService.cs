using ElectronNET.API;
using ElectronNET.API.Entities;
using ElectronNet.Hosting;
using ElectronNet.Infrastructure;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Events;

namespace ElectronNet.Services;

/// <summary>
/// 系统托盘：图标、悬停摘要与上下文菜单。
/// 菜单文案与今日摘要由渲染层推送（渲染层持有统计口径与 i18n），
/// 切换账号直接在 Host 执行，结果经 tray:command 事件通知渲染层。
/// </summary>
internal sealed class TrayService(
    IMainWindowAccessor mainWindowAccessor,
    SteamUserService steamUserService,
    SteamService steamService,
    SteamAppService steamAppService,
    IEventBus eventBus,
    ILogger<TrayService> logger)
{
    internal const int MaxStatusLines = 4;
    internal const int MaxAccounts = 20;
    internal const int MaxPages = 12;

    private readonly Lock _sync = new();
    private TrayStateRequest? _state;
    private string? _lastMenuSignature;
    private string? _lastToolTip;
    private bool _created;
    private int _switching;
    // SetMenuItems 先发送菜单、await 后才替换 C# 侧的点击注册表：并发调用会让注册表与
    // Electron 实际显示的菜单错位（点击 id 找不到处理器），因此所有托盘更新串行执行
    private Task _updates = Task.CompletedTask;

    /// <summary>
    /// 创建托盘（渲染层推送状态前先显示默认菜单）
    /// </summary>
    internal void Create()
    {
        var iconPath = IconPath();
        if (iconPath == null)
        {
            logger.LogError("Tray icon was not found; the system tray cannot be created");
            return;
        }

        var tray = Electron.Tray;
        Enqueue(async () =>
        {
            await tray.Show(iconPath).ConfigureAwait(false);
            // 点击事件必须在托盘创建之后注册：Host 端 register-tray-click 在 tray 尚不存在时会被静默丢弃，
            // 若与 Show 并发（Show 在队列里异步执行）左键单击将永远没有响应
            tray.OnClick += (_, _) => _ = ShowMainWindowAsync();
            await tray.SetToolTip("Steam Stat").ConfigureAwait(false);
            await tray.SetMenuItems(BuildMenu(null)).ConfigureAwait(false);
        });
        lock (_sync) _created = true;
        logger.LogInformation("System tray created");
    }

    /// <summary>
    /// 渲染层推送的托盘状态（今日摘要、当前游戏、账号与页面列表、菜单文案）
    /// </summary>
    internal void UpdateState(TrayStateRequest state)
    {
        var normalized = Normalize(state);
        string menuSignature;
        bool menuChanged;
        bool toolTipChanged;
        lock (_sync)
        {
            if (!_created) return;
            _state = normalized;
            menuSignature = MenuSignature(normalized);
            menuChanged = menuSignature != _lastMenuSignature;
            toolTipChanged = normalized.ToolTip != _lastToolTip;
            _lastMenuSignature = menuSignature;
            _lastToolTip = normalized.ToolTip;
        }

        if (toolTipChanged) Enqueue(() => Electron.Tray.SetToolTip(normalized.ToolTip));
        if (menuChanged) Enqueue(() => Electron.Tray.SetMenuItems(BuildMenu(normalized)));
    }

    private void Enqueue(Func<Task> update)
    {
        lock (_sync)
        {
            _updates = _updates.ContinueWith(async _ =>
            {
                try
                {
                    await update().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Failed to update the system tray");
                }
            }, TaskScheduler.Default).Unwrap();
        }
    }

    internal static TrayStateRequest Normalize(TrayStateRequest state) => state with
    {
        ToolTip = string.IsNullOrWhiteSpace(state.ToolTip) ? "Steam Stat" : state.ToolTip,
        StatusLines = state.StatusLines.Where(line => !string.IsNullOrWhiteSpace(line)).Take(MaxStatusLines).ToArray(),
        Accounts = state.Accounts.Take(MaxAccounts).ToArray(),
        Pages = state.Pages.Where(page => IsAppPath(page.Path)).Take(MaxPages).ToArray()
    };

    /// <summary>
    /// 仅允许应用内路由路径（以单个 / 开头，不含协议与反斜杠）
    /// </summary>
    internal static bool IsAppPath(string? path)
        => !string.IsNullOrWhiteSpace(path)
           && path.Length <= 128
           && path.StartsWith('/')
           && !path.StartsWith("//", StringComparison.Ordinal)
           && !path.Contains('\\')
           && !path.Contains(':');

    internal static string MenuSignature(TrayStateRequest state)
        => string.Join('\u001f',
            new[]
            {
                string.Join('\u001e', state.StatusLines),
                string.Join('\u001e', state.Accounts.Select(a => $"{a.SteamId}|{a.Label}|{a.Current}")),
                string.Join('\u001e', state.Pages.Select(p => $"{p.Path}|{p.Label}")),
                state.Labels.ShowWindow, state.Labels.SwitchAccount, state.Labels.OpenPage, state.Labels.Exit
            });

    private MenuItem[] BuildMenu(TrayStateRequest? state)
    {
        var items = new List<MenuItem>();
        if (state == null)
        {
            items.Add(new MenuItem { Label = "显示主窗口 (Show)", Click = () => _ = ShowMainWindowAsync() });
            items.Add(new MenuItem { Type = MenuType.separator });
            // 默认菜单只在渲染层推送托盘状态前出现，此时渲染层还无法处理退出流程，直接退出
            items.Add(new MenuItem { Label = "退出 (Exit) Steam Stat", Click = () => Electron.App.Quit() });
            return items.ToArray();
        }

        foreach (var line in state.StatusLines)
            items.Add(new MenuItem { Label = line, Enabled = false });
        if (state.StatusLines.Count > 0)
            items.Add(new MenuItem { Type = MenuType.separator });

        items.Add(new MenuItem { Label = state.Labels.ShowWindow, Click = () => _ = ShowMainWindowAsync() });

        if (state.Accounts.Count > 0)
        {
            // 子菜单父项必须显式声明 submenu 类型：Electron.NET 的 Tray 不会像 Menu 那样补全类型，
            // type=normal 的父项在 Windows 上只渲染成普通项，点击无反应
            items.Add(new MenuItem
            {
                Label = state.Labels.SwitchAccount,
                Type = MenuType.submenu,
                Submenu = state.Accounts.Select(account =>
                {
                    var steamId = account.SteamId;
                    var label = account.Label;
                    return new MenuItem
                    {
                        Label = label,
                        Type = MenuType.radio,
                        Checked = account.Current,
                        Click = () => _ = SwitchAccountAsync(steamId, label)
                    };
                }).ToArray()
            });
        }

        if (state.Pages.Count > 0)
        {
            items.Add(new MenuItem
            {
                Label = state.Labels.OpenPage,
                Type = MenuType.submenu,
                Submenu = state.Pages.Select(page =>
                {
                    var path = page.Path;
                    return new MenuItem { Label = page.Label, Click = () => _ = NavigateAsync(path) };
                }).ToArray()
            });
        }

        items.Add(new MenuItem { Type = MenuType.separator });
        items.Add(new MenuItem { Label = state.Labels.Exit, Click = () => _ = ExitAsync() });
        return items.ToArray();
    }

    /// <summary>
    /// 显示主窗口并导航到应用内页面（托盘菜单与系统通知点击共用）
    /// </summary>
    internal async Task NavigateAsync(string? path)
    {
        await ShowMainWindowAsync().ConfigureAwait(false);
        if (!IsAppPath(path)) return;
        await eventBus.PublishAsync(new TrayCommandIssued(new TrayCommandDto { Type = "navigate", Path = path }))
            .ConfigureAwait(false);
    }

    internal async Task ShowMainWindowAsync()
    {
        var snapshot = await mainWindowAccessor.GetSnapshotAsync().ConfigureAwait(false);
        if (snapshot.Availability != MainWindowAvailability.Available || snapshot.Window == null) return;
        try
        {
            var window = snapshot.Window;
            window.SetSkipTaskbar(false);
            // 最小化到任务栏时 Show() 不会还原窗口，需要显式 Restore
            if (await window.IsMinimizedAsync().ConfigureAwait(false)) window.Restore();
            window.Show();
            window.Focus();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to show the main window from the tray");
        }
    }

    /// <summary>
    /// 托盘「退出」：始终退出，但交给渲染层的退出流程处理（有正在记录的游戏时询问结束 / 丢弃记录），
    /// 不再直接关闭窗口绕过该流程。需要询问时先显示主窗口；渲染层不可用时直接退出。
    /// </summary>
    private async Task ExitAsync()
    {
        var snapshot = await mainWindowAccessor.GetSnapshotAsync().ConfigureAwait(false);
        if (snapshot.Availability != MainWindowAvailability.Available || snapshot.Window == null)
        {
            logger.LogInformation("Main window is unavailable; quitting directly from the tray");
            Electron.App.Quit();
            return;
        }

        try
        {
            if (steamAppService.GetAllRunning().Count > 0) await ShowMainWindowAsync().ConfigureAwait(false);
            await eventBus.PublishAsync(new TrayCommandIssued(new TrayCommandDto { Type = "exit" })).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to start the exit flow from the tray; quitting directly");
            Electron.App.Quit();
        }
    }

    private async Task SwitchAccountAsync(string steamId, string label)
    {
        // 切换账号会结束并重启 Steam 进程，重复点击时忽略
        if (Interlocked.Exchange(ref _switching, 1) == 1) return;
        try
        {
            var user = steamUserService.GetAll().FirstOrDefault(u => u.SteamId == steamId);
            var success = user != null && await steamService.ChangeSteamUser(new ChangeSteamUserRequest
            {
                Id = user.Id,
                SteamId = user.SteamId,
                AccountId = user.AccountId,
                AccountName = user.AccountName,
                PersonaName = user.PersonaName,
                RememberPassword = user.RememberPassword
            }).ConfigureAwait(false);
            logger.LogInformation("Tray account switch finished ({Success})", success);
            await eventBus.PublishAsync(new TrayCommandIssued(new TrayCommandDto
            {
                Type = "accountSwitched",
                Success = success,
                Label = label
            })).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to switch the Steam account from the tray");
        }
        finally
        {
            Interlocked.Exchange(ref _switching, 0);
        }
    }

    internal static string? IconPath() => PlatformRuntime.IconPath();
}
