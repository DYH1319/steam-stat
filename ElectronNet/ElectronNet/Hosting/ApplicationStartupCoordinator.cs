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
    DatabaseSnapshotter databaseSnapshotter,
    CredentialStorageScrubber credentialStorageScrubber,
    SteamLoginService loginService,
    SettingsCoordinator settingsCoordinator,
    GlobalStatusService globalStatusService,
    SteamUserService steamUserService,
    SteamAppService steamAppService,
    UseAppRecordService useAppRecordService,
    ElectronPowerEventsBridge powerEventsBridge,
    PlaytimeHistoryService playtimeHistoryService,
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

        // 每日滚动快照（后台执行，不阻塞启动；两份至少间隔 24h，最多保留 7 份）
        _ = databaseSnapshotter.SnapshotAsync();

        // 同步 / 初始化数据
        await globalStatusService.SyncDb(cancellationToken: cancellationToken);
        await steamUserService.SyncDb(cancellationToken);
        await steamAppService.InitDb(cancellationToken);
        await useAppRecordService.InitDb(cancellationToken);

        // 将历史明文登录凭证升级为加密存储
        var upgradedTokenCount = await loginService.EncryptLegacyTokensAsync();
        await credentialStorageScrubber.ScrubAsync(upgradedTokenCount, cancellationToken);

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

        // 按已保存的选项自动登录 Steam 账号（后台执行，不阻塞启动；失败只记日志）
        _ = loginService.AutoLoginEnabledTokensAsync(cancellationToken);

        // 桥接系统电源事件（睡眠切分会话）
        powerEventsBridge.Attach();

        // 定期快照 Steam 官方累计时长（基线与对账）
        playtimeHistoryService.StartSchedule();
    }
}
