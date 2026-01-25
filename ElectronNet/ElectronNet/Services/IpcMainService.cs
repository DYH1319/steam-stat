using ElectronNET.API;
using ElectronNet.Constants;
using ElectronNet.Jobs;

namespace ElectronNet.Services;

public static class IpcMainService
{
    /// <summary>
    /// 注册 IPC 通信处理器
    /// </summary>
    internal static void RegisterIpcHandlers()
    {
        var app = Electron.App;
        var ipcMain = Electron.IpcMain;
        var mainWindow = Program.ElectronMainWindow;

        #region 设置相关 API

        ipcMain.Handle("settings:get", (_) => SettingService.GetSettings());

        #endregion

        #region Steam 相关 API

        // Steam 状态页面
        ipcMain.Handle("steam:status:get", (_) => GlobalStatusService.GetOne());
        ipcMain.Handle("steam:status:refresh", async (_) => await GlobalStatusService.SyncAndGetOne());
        ipcMain.Handle("steam:libraryFolders:get", (_) => GlobalStatusService.GetLibraryFolders());

        // Steam 用户信息
        ipcMain.Handle("steam:loginUsers:get", (_) => SteamUserService.GetAll());
        ipcMain.Handle("steam:loginUsers:refresh", async (_) => await SteamUserService.SyncAndGetAll());

        // Steam 应用信息
        ipcMain.Handle("steam:runningApps:get", (_) => new { Apps = SteamAppService.GetAllRunning(), UpdateAppRunningStatusJob.LastUpdateTime });
        ipcMain.Handle("steam:appsInfo:get", (_) => SteamAppService.GetAll());
        ipcMain.Handle("steam:appsInfo:refresh", async (_) => await SteamAppService.SyncAndGetAll());

        // Steam 使用统计
        ipcMain.Handle("steam:validUseAppRecord:get", (obj) => new { Records = UseAppRecordService.GetAllValid(), UpdateAppRunningStatusJob.LastUpdateTime });

        #endregion

        // 应用窗口相关 API
        ipcMain.On("app:minimizeToTray", (_) =>
        {
            if (mainWindow != null)
            {
                mainWindow.Hide();
            }
        });

        ipcMain.On("app:quit", (_) =>
        {
            if (mainWindow != null)
            {
                app.Exit();
            }
        });

        #region Shell 相关 API

        ipcMain.On("shell:openExternal", (args) =>
        {
            if (args != null)
            {
                var url = args.ToString();
                Electron.Shell.OpenExternalAsync(url);
            }
        });

        ipcMain.On("shell:openPath", (args) =>
        {
            if (args != null)
            {
                var path = args.ToString();
                Electron.Shell.OpenPathAsync(path);
            }
        });

        #endregion

        Console.WriteLine($"{ConsoleLogPrefix.IPC} IPC handlers registered.");
    }
}