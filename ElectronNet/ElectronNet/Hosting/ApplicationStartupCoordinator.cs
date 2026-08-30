using ElectronNet.Services;

namespace ElectronNet.Hosting;

internal sealed class ApplicationStartupCoordinator(IpcMainService ipcMainService)
{
    /// <summary>
    /// 初始化 Electron App
    /// </summary>
    internal async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 执行数据库迁移
        await AppDbContext.Instance.ApplyMigrationsAsync();

        // 同步 / 初始化数据
        await GlobalStatusService.SyncDb();
        await SteamUserService.SyncDb();
        await SteamAppService.InitDb();
        await UseAppRecordService.InitDb();

        // 将历史明文登录凭证升级为加密存储
        await SteamLoginService.EncryptLegacyTokensAsync();

        // 初始化自动更新
        UpdateService.InitAutoUpdater();

        // 初始化设置和设置相关任务
        await Program.InitializeSettingsAndJobs();

        // 初始化界面内容
        await Program.InitializeContent();

        // 初始化主窗口
        await Program.InitializeMainWindow();

        // 添加监听器
        Program.AddAppListeners();
        Program.AddScreenListeners();
        Program.AddWindowListeners();

        // 创建系统托盘
        Program.CreateTray();

        // 注册 IPC 处理器
        ipcMainService.RegisterIpcHandlers();
    }
}
