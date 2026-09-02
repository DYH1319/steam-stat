using ElectronNet.Infrastructure;
using ElectronNet.Persistence;
using ElectronNet.Services;
using Microsoft.EntityFrameworkCore;
using SteamStat.Core.Events;
using SteamStat.Core.Features.Login;
using SteamStat.Core.Platform;
using SteamStat.Core.Settings;

namespace ElectronNet.Hosting;

internal sealed class ApplicationStartupCoordinator(
    IpcMainService ipcMainService,
    IEventBus eventBus,
    MainWindowAccessor mainWindowAccessor,
    DatabaseMigrator databaseMigrator,
    IDbContextFactory<AppDbContext> dbContextFactory,
    IHttpClientFactory httpClientFactory,
    ISteamInstallLocator installLocator,
    SteamLoginService loginService,
    SettingsCoordinator settingsCoordinator)
{
    /// <summary>
    /// 初始化 Electron App
    /// </summary>
    internal async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 执行数据库迁移
        await databaseMigrator.MigrateAsync(cancellationToken);

        // 同步 / 初始化数据
        await GlobalStatusService.SyncDb(dbContextFactory, installLocator);
        await SteamUserService.SyncDb(eventBus, dbContextFactory, httpClientFactory, installLocator);
        await SteamAppService.InitDb(dbContextFactory, installLocator);
        await UseAppRecordService.InitDb(dbContextFactory);

        // 将历史明文登录凭证升级为加密存储
        await loginService.EncryptLegacyTokensAsync();

        // 初始化自动更新
        UpdateService.InitAutoUpdater(eventBus);

        // 初始化设置和设置相关任务
        await settingsCoordinator.InitializeAsync(cancellationToken);

        // 初始化界面内容
        await Program.InitializeContent();

        // 初始化主窗口
        await Program.InitializeMainWindow(mainWindowAccessor, settingsCoordinator);

        // 添加监听器
        Program.AddAppListeners();
        Program.AddScreenListeners();
        Program.AddWindowListeners(settingsCoordinator);

        // 创建系统托盘
        Program.CreateTray();

        // 注册 IPC 处理器
        ipcMainService.RegisterIpcHandlers();
    }
}
