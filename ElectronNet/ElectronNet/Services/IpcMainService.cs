using ElectronNET.API;
using ElectronNet.Constants;
using ElectronNet.Hosting;
using ElectronNet.Infrastructure;
using ElectronNet.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Events;
using SteamStat.Core.Features.Friends;
using SteamStat.Core.Features.Library;
using SteamStat.Core.Features.Login;
using SteamStat.Core.Platform;
using SteamStat.Core.Settings;

namespace ElectronNet.Services;

// ReSharper disable ConvertClosureToMethodGroup
internal sealed class IpcMainService(
    IEventBus eventBus,
    IDbContextFactory<AppDbContext> dbContextFactory,
    IHttpClientFactory httpClientFactory,
    ISteamInstallLocator installLocator,
    IProcessController processController,
    TimeProvider timeProvider,
    IMainWindowAccessor mainWindowAccessor,
    SteamLoginService loginService,
    SteamLibraryService libraryService,
    SteamFriendsService friendsService,
    FriendStatusRecordService friendStatusRecordService,
    SettingsCoordinator settingsCoordinator,
    UpdateAppRunningStatusJob runningStatusJob,
    IpcRequestBinder requestBinder,
    ShellIpcPolicy shellPolicy,
    ILogger<IpcMainService> logger)
{
    /// <summary>
    /// 注册 IPC 通信处理器
    /// </summary>
    internal void RegisterIpcHandlers()
    {
        var app = Electron.App;
        var ipcMain = Electron.IpcMain;

        #region Steam 相关 API

        // Steam 状态页面
        Handle(ipcMain, SteamIpc.GetStatus, () => IpcDtoMapper.ToDto(GlobalStatusService.GetOne(dbContextFactory)));
        HandleAsync(ipcMain, SteamIpc.RefreshStatus, async () =>
            IpcDtoMapper.ToDto(await GlobalStatusService.SyncAndGetOne(dbContextFactory, installLocator)));
        Handle(ipcMain, SteamIpc.GetLibraryFolders, () => GlobalStatusService.GetLibraryFolders(installLocator));

        // Steam 用户信息
        Handle(ipcMain, SteamIpc.GetLoginUsers, () => SteamUserService.GetAll(dbContextFactory).Select(IpcDtoMapper.ToDto).ToArray());
        HandleAsync(ipcMain, SteamIpc.RefreshLoginUsers, async () =>
            (IReadOnlyList<SteamUserDto>)(await SteamUserService.SyncAndGetAll(
                eventBus, dbContextFactory, httpClientFactory, installLocator)).Select(IpcDtoMapper.ToDto).ToArray());
        HandleAsync(ipcMain, SteamIpc.ChangeLoginUser, request =>
            SteamService.ChangeSteamUser(request, dbContextFactory, installLocator, processController, timeProvider));

        // Steam 应用信息
        Handle(ipcMain, SteamIpc.GetRunningApps, () => new RunningAppsDto(
            SteamAppService.GetAllRunning(dbContextFactory).Select(IpcDtoMapper.ToDto).ToArray(),
            runningStatusJob.LastUpdateTime));
        Handle(ipcMain, SteamIpc.GetAppsInfo, request =>
            SteamAppService.GetAllWithQuery(request, dbContextFactory).Select(IpcDtoMapper.ToDto).ToArray());
        HandleAsync(ipcMain, SteamIpc.RefreshAppsInfo, async request =>
            (IReadOnlyList<SteamAppDto>)(await SteamAppService.SyncAndGetAllWithQuery(
                request, dbContextFactory, installLocator)).Select(IpcDtoMapper.ToDto).ToArray());

        // Steam 使用统计
        Handle(ipcMain, SteamIpc.GetValidUseAppRecords, request => new UseAppRecordsDto(
            UseAppRecordService.GetValidByParam(request, dbContextFactory),
            runningStatusJob.LastUpdateTime));
        Handle(ipcMain, SteamIpc.GetUsersInRecords, () =>
            SteamUserService.GetUsersInRecords(dbContextFactory).Select(IpcDtoMapper.ToDto).ToArray());
        HandleAsync(ipcMain, SteamIpc.EndUseAppRecording, () => UseAppRecordService.EndAllRecordings(dbContextFactory));
        HandleAsync(ipcMain, SteamIpc.DiscardUseAppRecording, () => UseAppRecordService.DiscardAllRecordings(dbContextFactory));

        // Steam 登录
        HandleAsync(ipcMain, SteamLoginIpc.StartCredentials, async request => IpcDtoMapper.ToDto(
            await loginService.LoginWithCredentials(request.Username, request.Password, request.RememberMe)));
        HandleAsync(ipcMain, SteamLoginIpc.StartQr, async request =>
            IpcDtoMapper.ToDto(await loginService.LoginWithQR(request.RememberMe)));
        HandleAsync(ipcMain, SteamLoginIpc.StartToken, async request =>
            IpcDtoMapper.ToDto(await loginService.LoginWithToken(request.TokenId)));
        Handle(ipcMain, SteamLoginIpc.SubmitGuardCode, request =>
        {
            loginService.SubmitGuardCode(request.Code);
            return true;
        });
        On(ipcMain, SteamLoginIpc.SwitchToUseCode, () => loginService.SwitchToUseCodeLogin());
        On(ipcMain, SteamLoginIpc.ConfirmDevice, () => loginService.ConfirmDeviceLogin());
        On(ipcMain, SteamLoginIpc.Cancel, () => loginService.CancelLogin());
        Handle(ipcMain, SteamLoginIpc.GetLoggedInUsers, () => loginService.GetLoggedInUsers());
        HandleAsync(ipcMain, SteamLoginIpc.LogoutUser, request => loginService.LogoutUser(request.AccountName));
        Handle(ipcMain, SteamLoginIpc.GetSavedTokens, () => loginService.GetSavedTokens().Select(IpcDtoMapper.ToDto).ToArray());
        HandleAsync(ipcMain, SteamLoginIpc.DeleteSavedToken, request => loginService.DeleteSavedToken(request.Id));
        Handle(ipcMain, SteamLoginIpc.SetPersonaState, request =>
            loginService.SetUserPersonaState(request.AccountName, request.PersonaState));

        // Steam 好友
        Handle(ipcMain, SteamFriendsIpc.GetAll, () => friendsService.GetAllLoggedInUsersFriends()
            .Select(data => IpcDtoMapper.ToDto(data)!).ToArray());
        Handle(ipcMain, SteamFriendsIpc.GetForUser, request =>
            IpcDtoMapper.ToDto(friendsService.GetFriendsForUser(request.AccountName)));
        Handle(ipcMain, SteamFriendsIpc.GetCached, () => friendsService.GetCachedFriendsData()
            .Select(data => IpcDtoMapper.ToDto(data)!).ToArray());
        On(ipcMain, SteamFriendsIpc.RequestFriendInfo, request =>
            friendsService.RequestFriendInfo(request.AccountName, request.FriendSteamId));

        // 好友状态变化记录
        Handle(ipcMain, SteamFriendsIpc.StartTracking, request =>
            friendStatusRecordService.StartTracking(request.AccountName, request.FriendSteamIds));
        Handle(ipcMain, SteamFriendsIpc.StopTracking, request =>
            friendStatusRecordService.StopTracking(request.AccountName, request.FriendSteamIds));
        Handle(ipcMain, SteamFriendsIpc.GetTracking, request =>
            friendStatusRecordService.GetTrackedFriends(request.AccountName));
        Handle(ipcMain, SteamFriendsIpc.GetAllTracking, () => friendStatusRecordService.GetAllTrackedFriends()
            .ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value));
        Handle(ipcMain, SteamFriendsIpc.GetRecords, request =>
            friendStatusRecordService.GetRecords(request).Select(IpcDtoMapper.ToDto).ToArray());
        HandleAsync(ipcMain, SteamFriendsIpc.ClearRecords, request =>
            friendStatusRecordService.ClearRecordsAsync(request));

        // Steam 游戏库
        HandleAsync(ipcMain, SteamLibraryIpc.GetForUser, async request =>
            (IReadOnlyList<SteamOwnedGameDto>)(await libraryService.GetLibraryForUserAsync(request.AccountName))
            .Select(IpcDtoMapper.ToDto).ToArray());
        HandleAsync(ipcMain, SteamLibraryIpc.GetForAllUsers, async () =>
            (IReadOnlyDictionary<string, IReadOnlyList<SteamOwnedGameDto>>)(await libraryService.GetLibraryForAllUsersAsync())
            .ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<SteamOwnedGameDto>)pair.Value.Select(IpcDtoMapper.ToDto).ToArray()));
        HandleAsync(ipcMain, SteamLibraryIpc.SyncForUser, request =>
            libraryService.SyncLibraryForUserAsync(request.AccountName));
        HandleAsync(ipcMain, SteamLibraryIpc.SyncForAllUsers, async () =>
            (IReadOnlyDictionary<string, bool>)await libraryService.SyncLibraryForAllUsersAsync());

        #endregion

        #region Job 相关 API

        Handle(ipcMain, JobIpc.GetUpdateAppRunningStatus, () => runningStatusJob.GetStatus());

        #endregion

        #region Setting 相关 API

        Handle(ipcMain, SettingIpc.Get, () => IpcDtoMapper.ToDto(settingsCoordinator.GetSettings()));
        HandleAsync(ipcMain, SettingIpc.Update, request => settingsCoordinator.UpdateSettingsAsync(IpcDtoMapper.ToCore(request)));

        #endregion

        #region Updater 相关 API

        HandleAsync(ipcMain, UpdaterIpc.GetStatus, () => UpdateService.GetStatus());
        On(ipcMain, UpdaterIpc.Check, () => UpdateService.CheckForUpdate());
        On(ipcMain, UpdaterIpc.Download, () => UpdateService.DownloadUpdate());
        On(ipcMain, UpdaterIpc.QuitAndInstall, () => UpdateService.QuitAndInstall());

        #endregion

        #region App & Window 相关 API

        On(ipcMain, AppWindowIpc.Quit, () => app.Quit());

        On(ipcMain, AppWindowIpc.MinimizeToTray, () =>
        {
            _ = ExecuteWindowActionAsync(window =>
            {
                window.Hide();
                window.SetSkipTaskbar(true);
            });
        });

        On(ipcMain, AppWindowIpc.Minimize, () =>
        {
            _ = ExecuteWindowActionAsync(window => window.Minimize());
        });

        HandleAsync(ipcMain, AppWindowIpc.Maximize, ToggleMaximizeAsync);

        On(ipcMain, AppWindowIpc.Close, () =>
        {
            _ = ExecuteWindowActionAsync(window => window.Close());
        });
        HandleAsync(ipcMain, AppWindowIpc.IsMaximized, IsMaximizedAsync);

        #endregion

        #region Shell 相关 API

        On(ipcMain, ShellIpc.OpenExternal, value =>
        {
            if (ShellIpcPolicy.IsAllowedExternalUrl(value))
                _ = OpenExternalAsync(value);
            else
                logger.LogWarning("Rejected shell external URL with an unsupported or invalid scheme");
        });

        On(ipcMain, ShellIpc.OpenPath, value =>
        {
            if (shellPolicy.IsAllowedPath(value))
                _ = OpenPathAsync(value);
            else
                logger.LogWarning("Rejected shell path that was not produced by the application");
        });

        #endregion

        Console.WriteLine($"{ConsoleLogPrefix.IPC} IPC handlers registered.");
    }

    private async Task ExecuteWindowActionAsync(Action<BrowserWindow> action)
    {
        var snapshot = await mainWindowAccessor.GetSnapshotAsync().ConfigureAwait(false);
        if (snapshot.Availability != MainWindowAvailability.Available || snapshot.Window == null) return;
        try
        {
            action(snapshot.Window);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to execute an Electron window action");
        }
    }

    private async Task<bool> ToggleMaximizeAsync()
    {
        var snapshot = await mainWindowAccessor.GetSnapshotAsync().ConfigureAwait(false);
        if (snapshot.Availability != MainWindowAvailability.Available || snapshot.Window == null) return false;
        try
        {
            var isMaximized = await snapshot.Window.IsMaximizedAsync().ConfigureAwait(false);
            if (isMaximized) snapshot.Window.Unmaximize();
            else snapshot.Window.Maximize();
            return !isMaximized;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to toggle the Electron window maximize state");
            return false;
        }
    }

    private async Task<bool> IsMaximizedAsync()
    {
        var snapshot = await mainWindowAccessor.GetSnapshotAsync().ConfigureAwait(false);
        if (snapshot.Availability != MainWindowAvailability.Available || snapshot.Window == null) return false;
        try
        {
            return await snapshot.Window.IsMaximizedAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to read the Electron window maximize state");
            return false;
        }
    }

    private async Task OpenExternalAsync(string value)
    {
        try
        {
            await Electron.Shell.OpenExternalAsync(value).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to open an approved external URL");
        }
    }

    private async Task OpenPathAsync(string value)
    {
        try
        {
            await Electron.Shell.OpenPathAsync(Path.GetFullPath(value)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to open an approved application path");
        }
    }

    private static void Handle<TResponse>(
        IpcMain ipcMain,
        IpcInvoke<IpcNoRequest, TResponse> endpoint,
        Func<TResponse> handler)
        => ipcMain.Handle(endpoint.Channel, _ => handler()!);

    private void Handle<TRequest, TResponse>(
        IpcMain ipcMain,
        IpcInvoke<TRequest, TResponse> endpoint,
        Func<TRequest, TResponse> handler)
        => ipcMain.Handle(endpoint.Channel, value => handler(requestBinder.Bind<TRequest>(value, endpoint))!);

    private static void HandleAsync<TResponse>(
        IpcMain ipcMain,
        IpcInvoke<IpcNoRequest, TResponse> endpoint,
        Func<Task<TResponse>> handler)
        => ipcMain.Handle(endpoint.Channel, async _ => (object)(await handler())!);

    private void HandleAsync<TRequest, TResponse>(
        IpcMain ipcMain,
        IpcInvoke<TRequest, TResponse> endpoint,
        Func<TRequest, Task<TResponse>> handler)
        => ipcMain.Handle(endpoint.Channel, async value =>
            (object)(await handler(requestBinder.Bind<TRequest>(value, endpoint)))!);

    private static void On(
        IpcMain ipcMain,
        IpcSend<IpcNoRequest> endpoint,
        Action handler)
        => _ = ipcMain.On(endpoint.Channel, _ => handler());

    private void On<TRequest>(
        IpcMain ipcMain,
        IpcSend<TRequest> endpoint,
        Action<TRequest> handler)
        => _ = ipcMain.On(endpoint.Channel, value => handler(requestBinder.Bind<TRequest>(value, endpoint)));
}
