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
    WindowStateStore windowState,
    PopoutWindowService popoutWindows,
    PendingRestoreApplier pendingRestoreApplier,
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
    ScreenshotArchiveService screenshotArchiveService,
    SaveBackupService saveBackupService,
    ProfileHistoryService profileHistoryService,
    SyncService syncService,
    PublicDataService publicDataService,
    TrayService trayService,
    RendererProxySync rendererProxySync,
    ILogger<Program> logger)
{
    /// <summary>
    /// 初始化 Electron App
    /// </summary>
    internal async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 应用上次暂存的备份恢复（必须在打开数据库之前；旧版本的备份随后由迁移升级）
        await pendingRestoreApplier.ApplyAsync(cancellationToken);

        // 执行数据库迁移
        await databaseMigrator.MigrateAsync(cancellationToken);

        // 多设备同步：安装变更捕获触发器（幂等）并开始定时同步（未连接时什么都不做）
        await syncService.InitializeAsync(cancellationToken);

        // 公共数据层：启动 2 分钟后检查一次，之后每 6 小时检查是否需要刷新（关闭时什么都不做）
        publicDataService.StartSchedule();

        // 每日滚动快照（后台执行，不阻塞启动；两份至少间隔 24h，最多保留 7 份）
        _ = databaseSnapshotter.SnapshotAsync();

        // 同步 / 初始化数据
        await globalStatusService.SyncDb(cancellationToken: cancellationToken);
        await steamUserService.SyncDb(cancellationToken);
        // 等级和头像框来自公开接口，部分网络下不通，不能阻塞启动
        _ = steamUserService.SyncDecorationsAsync(cancellationToken);
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
        await Program.InitializeMainWindow(mainWindowAccessor, settingsCoordinator, windowState, logger);
        // 独立窗口没有托盘入口和菜单，主窗口没了就不该留着
        Program.ElectronMainWindow!.OnClosed += popoutWindows.CloseAll;

        // 渲染层（Chromium 会话）代理与网络设置保持一致
        await rendererProxySync.AttachAsync();

        // 添加监听器
        Program.AddAppListeners();
        Program.AddWindowListeners(settingsCoordinator, logger);

        // 创建系统托盘
        trayService.Create();

        // 注册 IPC 处理器
        ipcMainService.RegisterIpcHandlers();

        // 按已保存的选项自动登录 Steam 账号（后台执行，不阻塞启动；失败只记日志）
        _ = loginService.AutoLoginEnabledTokensAsync(cancellationToken);

        // 桥接系统电源事件（睡眠切分会话）
        powerEventsBridge.Attach();

        // 定期快照 Steam 官方累计时长（基线与对账）
        playtimeHistoryService.StartSchedule();

        // 档案扩展：截图索引、存档快照、资料变化（均在后台延迟启动）
        screenshotArchiveService.StartSchedule();
        saveBackupService.StartSchedule();
        profileHistoryService.StartSchedule();
    }
}
