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
        ipcMain.Handle("steam:validUseAppRecord:get", (param) => new { Records = UseAppRecordService.GetValidByParam(param), UpdateAppRunningStatusJob.LastUpdateTime });
        ipcMain.Handle("steam:usersInRecords:get", (_) => SteamUserService.GetUsersInRecords());

        #endregion

        #region 设置相关 API

        ipcMain.Handle("setting:get", (_) => SettingService.GetSettings());
        ipcMain.Handle("setting:update", async (param) => await SettingService.UpdateSettings(param));

        #endregion

        #region Job 相关 API

        ipcMain.Handle("job:updateAppRunningStatus:get", (_) => UpdateAppRunningStatusJob.GetStatus());

        #endregion

        #region Updater 相关 API

        ipcMain.Handle("updater:status:get", async (_) => await UpdateService.GetStatus());

        #endregion

        #region 应用窗体相关 API

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

        ipcMain.On("window:minimize", (_) => mainWindow?.Minimize());

        ipcMain.Handle("window:maximize", async (_) =>
        {
            if (mainWindow == null) return false;
            var isMaximized = await mainWindow.IsMaximizedAsync();
            if (isMaximized)
            {
                mainWindow.Unmaximize();
            }
            else
            {
                mainWindow.Maximize();
            }

            return !isMaximized;
        });

        ipcMain.On("window:close", (_) => mainWindow?.Close());

        ipcMain.Handle("window:isMaximized", async (_) => mainWindow != null && await mainWindow.IsMaximizedAsync());

        #endregion

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