using ElectronNET.API;
using ElectronNet.Constants;
using ElectronNet.Jobs;

namespace ElectronNet.Services;

// ReSharper disable ConvertClosureToMethodGroup
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
        ipcMain.Handle("steam:loginUser:change", async (param) => await SteamService.ChangeSteamUser(param));

        // Steam 应用信息
        ipcMain.Handle("steam:runningApps:get", (_) => new { Apps = SteamAppService.GetAllRunning(), UpdateAppRunningStatusJob.LastUpdateTime });
        ipcMain.Handle("steam:appsInfo:get", (param) => SteamAppService.GetAllWithQuery(param));
        ipcMain.Handle("steam:appsInfo:refresh", async (param) => await SteamAppService.SyncAndGetAllWithQuery(param));

        // Steam 使用统计
        ipcMain.Handle("steam:validUseAppRecord:get", (param) => new { Records = UseAppRecordService.GetValidByParam(param), UpdateAppRunningStatusJob.LastUpdateTime });
        ipcMain.Handle("steam:usersInRecords:get", (_) => SteamUserService.GetUsersInRecords());
        ipcMain.Handle("steam:useAppRecording:end", async (_) => await UseAppRecordService.EndAllRecordings());
        ipcMain.Handle("steam:useAppRecording:discard", async (_) => await UseAppRecordService.DiscardAllRecordings());

        // Steam 登录
        ipcMain.Handle("steamLogin:credentials:start", async (param) =>
        {
            var pd = param as Dictionary<string, object> ?? [];
            return await SteamLoginService.LoginWithCredentials(
                pd.GetValueOrDefault("username")?.ToString() ?? "",
                pd.GetValueOrDefault("password")?.ToString() ?? "",
                Convert.ToBoolean(pd.GetValueOrDefault("rememberMe", false))
            );
        });
        ipcMain.Handle("steamLogin:qr:start", async (param) =>
        {
            var pd = param as Dictionary<string, object> ?? [];
            return await SteamLoginService.LoginWithQR(
                Convert.ToBoolean(pd.GetValueOrDefault("rememberMe", false))
            );
        });
        ipcMain.Handle("steamLogin:token:start", async (param) =>
        {
            var pd = param as Dictionary<string, object> ?? [];
            return await SteamLoginService.LoginWithToken(
                Convert.ToInt32(pd.GetValueOrDefault("tokenId", 0))
            );
        });
        ipcMain.Handle("steamLogin:guardCode:submit", (param) =>
        {
            var pd = param as Dictionary<string, object> ?? [];
            SteamLoginService.SubmitGuardCode(pd.GetValueOrDefault("code")?.ToString() ?? "");
            return true;
        });
        ipcMain.On("steamLogin:switchToUseCode", (_) => SteamLoginService.SwitchToUseCodeLogin());
        ipcMain.On("steamLogin:confirmDevice", (_) => SteamLoginService.ConfirmDeviceLogin());
        ipcMain.On("steamLogin:cancel", (_) => SteamLoginService.CancelLogin());
        ipcMain.Handle("steamLogin:loggedInUsers:get", (_) => SteamLoginService.GetLoggedInUsers());
        ipcMain.Handle("steamLogin:user:logout", async (param) =>
        {
            var pd = param as Dictionary<string, object> ?? [];
            return await SteamLoginService.LogoutUser(pd.GetValueOrDefault("accountName")?.ToString() ?? "");
        });
        ipcMain.Handle("steamLogin:savedTokens:get", (_) => SteamLoginService.GetSavedTokens());
        ipcMain.Handle("steamLogin:savedToken:delete", async (param) =>
        {
            var pd = param as Dictionary<string, object> ?? [];
            return await SteamLoginService.DeleteSavedToken(
                Convert.ToInt32(pd.GetValueOrDefault("id", 0))
            );
        });

        #endregion

        #region Job 相关 API

        ipcMain.Handle("job:updateAppRunningStatus:get", (_) => UpdateAppRunningStatusJob.GetStatus());

        #endregion

        #region Setting 相关 API

        ipcMain.Handle("setting:get", (_) => SettingService.GetSettings());
        ipcMain.Handle("setting:update", async (param) => await SettingService.UpdateSettings(param));

        #endregion

        #region Updater 相关 API

        ipcMain.Handle("updater:status:get", async (_) => await UpdateService.GetStatus());
        ipcMain.On("updater:check", (_) => UpdateService.CheckForUpdate());
        ipcMain.On("updater:download", (_) => UpdateService.DownloadUpdate());
        ipcMain.On("updater:quitAndInstall", (_) => UpdateService.QuitAndInstall());

        #endregion

        #region App & Window 相关 API

        ipcMain.On("app:quit", (_) => app.Quit());

        ipcMain.On("window:minimizeToTray", (_) =>
        {
            if (mainWindow != null)
            {
                mainWindow.Hide();
                mainWindow.SetSkipTaskbar(true);
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
