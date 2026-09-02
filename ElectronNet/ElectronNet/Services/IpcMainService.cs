using ElectronNET.API;
using ElectronNet.Constants;
using ElectronNet.Jobs;
using Microsoft.EntityFrameworkCore;
using SteamStat.Core.Events;
using SteamStat.Core.Features.Friends;
using SteamStat.Core.Features.Library;
using SteamStat.Core.Features.Login;
using SteamStat.Core.Platform;
using SteamStat.Core.Settings;

namespace ElectronNet.Services;

// ReSharper disable ConvertClosureToMethodGroup
public sealed class IpcMainService(
    IEventBus eventBus,
    IDbContextFactory<AppDbContext> dbContextFactory,
    IHttpClientFactory httpClientFactory,
    ISteamInstallLocator installLocator,
    IProcessController processController,
    TimeProvider timeProvider,
    SteamLoginService loginService,
    SteamLibraryService libraryService,
    SteamFriendsService friendsService,
    FriendStatusRecordService friendStatusRecordService,
    SettingsCoordinator settingsCoordinator,
    UpdateAppRunningStatusJob runningStatusJob)
{
    /// <summary>
    /// 注册 IPC 通信处理器
    /// </summary>
    internal void RegisterIpcHandlers()
    {
        var app = Electron.App;
        var ipcMain = Electron.IpcMain;
        var mainWindow = Program.ElectronMainWindow;

        #region Steam 相关 API

        // Steam 状态页面
        ipcMain.Handle("steam:status:get", (_) => GlobalStatusService.GetOne(dbContextFactory));
        ipcMain.Handle("steam:status:refresh", async (_) => await GlobalStatusService.SyncAndGetOne(dbContextFactory, installLocator));
        ipcMain.Handle("steam:libraryFolders:get", (_) => GlobalStatusService.GetLibraryFolders(installLocator));

        // Steam 用户信息
        ipcMain.Handle("steam:loginUsers:get", (_) => SteamUserService.GetAll(dbContextFactory));
        ipcMain.Handle("steam:loginUsers:refresh", async (_) => await SteamUserService.SyncAndGetAll(eventBus, dbContextFactory, httpClientFactory, installLocator));
        ipcMain.Handle("steam:loginUser:change", async (param) => await SteamService.ChangeSteamUser(param, dbContextFactory, installLocator, processController, timeProvider));

        // Steam 应用信息
        ipcMain.Handle("steam:runningApps:get", (_) => new { Apps = SteamAppService.GetAllRunning(dbContextFactory), runningStatusJob.LastUpdateTime });
        ipcMain.Handle("steam:appsInfo:get", (param) => SteamAppService.GetAllWithQuery(param, dbContextFactory));
        ipcMain.Handle("steam:appsInfo:refresh", async (param) => await SteamAppService.SyncAndGetAllWithQuery(param, dbContextFactory, installLocator));

        // Steam 使用统计
        ipcMain.Handle("steam:validUseAppRecord:get", (param) => new { Records = UseAppRecordService.GetValidByParam(param, dbContextFactory), runningStatusJob.LastUpdateTime });
        ipcMain.Handle("steam:usersInRecords:get", (_) => SteamUserService.GetUsersInRecords(dbContextFactory));
        ipcMain.Handle("steam:useAppRecording:end", async (_) => await UseAppRecordService.EndAllRecordings(dbContextFactory));
        ipcMain.Handle("steam:useAppRecording:discard", async (_) => await UseAppRecordService.DiscardAllRecordings(dbContextFactory));

        // Steam 登录
        ipcMain.Handle("steamLogin:credentials:start", async (param) =>
        {
            var pd = param as Dictionary<string, object> ?? [];
            return await loginService.LoginWithCredentials(
                pd.GetValueOrDefault("username")?.ToString() ?? "",
                pd.GetValueOrDefault("password")?.ToString() ?? "",
                Convert.ToBoolean(pd.GetValueOrDefault("rememberMe", false))
            );
        });
        ipcMain.Handle("steamLogin:qr:start", async (param) =>
        {
            var pd = param as Dictionary<string, object> ?? [];
            return await loginService.LoginWithQR(
                Convert.ToBoolean(pd.GetValueOrDefault("rememberMe", false))
            );
        });
        ipcMain.Handle("steamLogin:token:start", async (param) =>
        {
            var pd = param as Dictionary<string, object> ?? [];
            return await loginService.LoginWithToken(
                Convert.ToInt32(pd.GetValueOrDefault("tokenId", 0))
            );
        });
        ipcMain.Handle("steamLogin:guardCode:submit", (param) =>
        {
            var pd = param as Dictionary<string, object> ?? [];
            loginService.SubmitGuardCode(pd.GetValueOrDefault("code")?.ToString() ?? "");
            return true;
        });
        ipcMain.On("steamLogin:switchToUseCode", (_) => loginService.SwitchToUseCodeLogin());
        ipcMain.On("steamLogin:confirmDevice", (_) => loginService.ConfirmDeviceLogin());
        ipcMain.On("steamLogin:cancel", (_) => loginService.CancelLogin());
        ipcMain.Handle("steamLogin:loggedInUsers:get", (_) => loginService.GetLoggedInUsers());
        ipcMain.Handle("steamLogin:user:logout", async (param) =>
        {
            var pd = param as Dictionary<string, object> ?? [];
            return await loginService.LogoutUser(pd.GetValueOrDefault("accountName")?.ToString() ?? "");
        });
        ipcMain.Handle("steamLogin:savedTokens:get", (_) => loginService.GetSavedTokens());
        ipcMain.Handle("steamLogin:savedToken:delete", async (param) =>
        {
            var pd = param as Dictionary<string, object> ?? [];
            return await loginService.DeleteSavedToken(
                Convert.ToInt32(pd.GetValueOrDefault("id", 0))
            );
        });
        ipcMain.Handle("steamLogin:user:setPersonaState", (param) =>
        {
            var pd = param as Dictionary<string, object> ?? [];
            return loginService.SetUserPersonaState(
                pd.GetValueOrDefault("accountName")?.ToString() ?? "",
                Convert.ToInt32(pd.GetValueOrDefault("personaState", 0))
            );
        });

        // Steam 好友
        ipcMain.Handle("steamFriends:getAll", (_) => friendsService.GetAllLoggedInUsersFriends());
        ipcMain.Handle("steamFriends:getForUser", (param) =>
        {
            var pd = param as Dictionary<string, object> ?? [];
            return friendsService.GetFriendsForUser(
                pd.GetValueOrDefault("accountName")?.ToString() ?? ""
            );
        });
        ipcMain.Handle("steamFriends:getCached", (_) => friendsService.GetCachedFriendsData());
        ipcMain.On("steamFriends:requestFriendInfo", (param) =>
        {
            var pd = param as Dictionary<string, object> ?? [];
            friendsService.RequestFriendInfo(
                pd.GetValueOrDefault("accountName")?.ToString() ?? "",
                pd.GetValueOrDefault("friendSteamId")?.ToString() ?? ""
            );
        });

        // 好友状态变化记录
        ipcMain.Handle("steamFriends:track:start", (param) =>
        {
            var pd = param as Dictionary<string, object> ?? [];
            var accountName = pd.GetValueOrDefault("accountName")?.ToString() ?? "";
            var friendIds = (pd.GetValueOrDefault("friendSteamIds") as IEnumerable<object>)
                ?.Select(o => o?.ToString() ?? "")
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList() ?? [];
            return friendStatusRecordService.StartTracking(accountName, friendIds);
        });
        ipcMain.Handle("steamFriends:track:stop", (param) =>
        {
            var pd = param as Dictionary<string, object> ?? [];
            var accountName = pd.GetValueOrDefault("accountName")?.ToString() ?? "";
            var friendIds = (pd.GetValueOrDefault("friendSteamIds") as IEnumerable<object>)
                ?.Select(o => o?.ToString() ?? "")
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList() ?? [];
            return friendStatusRecordService.StopTracking(accountName, friendIds);
        });
        ipcMain.Handle("steamFriends:track:get", (param) =>
        {
            var pd = param as Dictionary<string, object> ?? [];
            var accountName = pd.GetValueOrDefault("accountName")?.ToString() ?? "";
            return friendStatusRecordService.GetTrackedFriends(accountName);
        });
        ipcMain.Handle("steamFriends:track:getAll", (_) => friendStatusRecordService.GetAllTrackedFriends());
        ipcMain.Handle("steamFriends:records:get", (param) => friendStatusRecordService.GetRecords(param));
        ipcMain.Handle("steamFriends:records:clear", async (param) => await friendStatusRecordService.ClearRecordsAsync(param));

        // Steam 游戏库
        ipcMain.Handle("steamLibrary:getForUser", async (param) =>
        {
            var pd = param as Dictionary<string, object> ?? [];
            return await libraryService.GetLibraryForUserAsync(
                pd.GetValueOrDefault("accountName")?.ToString() ?? ""
            );
        });
        ipcMain.Handle("steamLibrary:getForAllUsers", async (_) => await libraryService.GetLibraryForAllUsersAsync());
        ipcMain.Handle("steamLibrary:syncForUser", async (param) =>
        {
            var pd = param as Dictionary<string, object> ?? [];
            return await libraryService.SyncLibraryForUserAsync(
                pd.GetValueOrDefault("accountName")?.ToString() ?? ""
            );
        });
        ipcMain.Handle("steamLibrary:syncForAllUsers", async (_) => await libraryService.SyncLibraryForAllUsersAsync());

        #endregion

        #region Job 相关 API

        ipcMain.Handle("job:updateAppRunningStatus:get", (_) => runningStatusJob.GetStatus());

        #endregion

        #region Setting 相关 API

        ipcMain.Handle("setting:get", (_) => settingsCoordinator.GetSettings());
        ipcMain.Handle("setting:update", async (param) => await settingsCoordinator.UpdateSettingsAsync(param));

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
