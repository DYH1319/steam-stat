using ElectronNet.Infrastructure;
using ElectronNet.Persistence;
using ElectronNet.Services;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Features.Login;
using SteamStat.Core.Settings;

namespace ElectronNet.Hosting;

internal sealed class ApplicationStartupCoordinator(
    IpcMainService ipcMainService,
    MainWindowAccessor mainWindowAccessor,
    DatabaseMigrator databaseMigrator,
    SteamLoginService loginService,
    SettingsCoordinator settingsCoordinator,
    GlobalStatusService globalStatusService,
    SteamUserService steamUserService,
    SteamAppService steamAppService,
    UseAppRecordService useAppRecordService,
    ILogger<Program> logger)
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
        await globalStatusService.SyncDb(cancellationToken: cancellationToken);
        await steamUserService.SyncDb(cancellationToken);
        await steamAppService.InitDb(cancellationToken);
        await useAppRecordService.InitDb(cancellationToken);

        // 将历史明文登录凭证升级为加密存储
        await loginService.EncryptLegacyTokensAsync();

        // 初始化设置和设置相关任务
        await settingsCoordinator.InitializeAsync(cancellationToken);

        // 初始化界面内容
        await Program.InitializeContent(logger);

        // 初始化主窗口
        await Program.InitializeMainWindow(mainWindowAccessor, settingsCoordinator, logger);

        // 添加监听器
        Program.AddAppListeners();
        Program.AddScreenListeners();
        Program.AddWindowListeners(settingsCoordinator, logger);

        // 创建系统托盘
        Program.CreateTray(logger);

        // 注册 IPC 处理器
        ipcMainService.RegisterIpcHandlers();
    }
}
