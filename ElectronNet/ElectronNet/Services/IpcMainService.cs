using ElectronNET.API;
using ElectronNet.Hosting;
using ElectronNet.Infrastructure;
using ElectronNet.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Events;
using SteamStat.Core.Features.Achievements;
using SteamStat.Core.Features.Friends;
using SteamStat.Core.Features.Library;
using SteamStat.Core.Features.Login;
using SteamStat.Core.Platform;
using SteamStat.Core.Settings;
using SteamStat.Core.Steam;

namespace ElectronNet.Services;

// ReSharper disable ConvertClosureToMethodGroup
internal sealed class IpcMainService(
    IMainWindowAccessor mainWindowAccessor,
    GlobalStatusService globalStatusService,
    SteamUserService steamUserService,
    SteamService steamService,
    SteamAppService steamAppService,
    UseAppRecordService useAppRecordService,
    UpdateService updateService,
    SteamLoginService loginService,
    SteamLibraryService libraryService,
    SteamAchievementsService achievementsService,
    SteamFriendsService friendsService,
    SteamFriendCompareService friendCompareService,
    SteamOperationalStatusService operationalStatusService,
    FriendStatusRecordService friendStatusRecordService,
    LocalDataSourceService localDataSourceService,
    SessionManagementService sessionManagementService,
    PlaytimeHistoryService playtimeHistoryService,
    SessionImportService sessionImportService,
    StatsDatasetService statsDatasetService,
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
        Handle(ipcMain, SteamIpc.GetStatus, () => IpcDtoMapper.ToDto(globalStatusService.GetOne()));
        HandleAsync(ipcMain, SteamIpc.RefreshStatus, async () =>
            IpcDtoMapper.ToDto(await globalStatusService.SyncAndGetOne()));
        Handle(ipcMain, SteamIpc.GetLibraryFolders, globalStatusService.GetLibraryFolders);
        HandleAsync(ipcMain, SteamIpc.GetOperationalStatus, async () =>
            IpcDtoMapper.ToDto(await operationalStatusService.GetAsync()));
        HandleAsync(ipcMain, SteamIpc.GetLocalDataSources, () => localDataSourceService.GetAsync());

        // Steam 用户信息
        Handle(ipcMain, SteamIpc.GetLoginUsers, () => steamUserService.GetAll().Select(IpcDtoMapper.ToDto).ToArray());
        HandleAsync(ipcMain, SteamIpc.RefreshLoginUsers, async () =>
            (IReadOnlyList<SteamUserDto>)(await steamUserService.SyncAndGetAll()).Select(IpcDtoMapper.ToDto).ToArray());
        HandleAsync(ipcMain, SteamIpc.ChangeLoginUser, steamService.ChangeSteamUser);

        // Steam 应用信息
        Handle(ipcMain, SteamIpc.GetRunningApps, () => new RunningAppsDto(
            steamAppService.GetAllRunning().Select(IpcDtoMapper.ToDto).ToArray(),
            runningStatusJob.LastUpdateTime));
        Handle(ipcMain, SteamIpc.GetAppsInfo, request =>
            steamAppService.GetAllWithQuery(request).Select(IpcDtoMapper.ToDto).ToArray());
        HandleAsync(ipcMain, SteamIpc.RefreshAppsInfo, async request =>
            (IReadOnlyList<SteamAppDto>)(await steamAppService.SyncAndGetAllWithQuery(request)).Select(IpcDtoMapper.ToDto).ToArray());

        // Steam 使用统计
        Handle(ipcMain, SteamIpc.GetValidUseAppRecords, request => new UseAppRecordsDto(
            useAppRecordService.GetValidByParam(request),
            runningStatusJob.LastUpdateTime));
        Handle(ipcMain, SteamIpc.GetUsersInRecords, () =>
            steamUserService.GetUsersInRecords().Select(IpcDtoMapper.ToDto).ToArray());
        HandleAsync(ipcMain, SteamIpc.EndUseAppRecording, () => runningStatusJob.EndAllRecordingsAsync(false));
        HandleAsync(ipcMain, SteamIpc.DiscardUseAppRecording, () => runningStatusJob.EndAllRecordingsAsync(true));

        // 会话管理
        Handle(ipcMain, SessionIpc.GetList, request => sessionManagementService.GetList(request));
        HandleAsync(ipcMain, SessionIpc.Update, request => sessionManagementService.Update(request));
        HandleAsync(ipcMain, SessionIpc.Merge, request => sessionManagementService.Merge(request));
        HandleAsync(ipcMain, SessionIpc.Delete, request => sessionManagementService.Delete(request));
        Handle(ipcMain, SessionIpc.GetTags, () => sessionManagementService.GetTags());
        Handle(ipcMain, SessionIpc.GetApps, () => sessionManagementService.GetApps());

        // 历史导入与对账
        Handle(ipcMain, HistoryIpc.GetOverview, request => playtimeHistoryService.GetOverview(request));
        HandleAsync(ipcMain, HistoryIpc.Capture, () => playtimeHistoryService.CaptureAsync());
        Handle(ipcMain, HistoryIpc.PreviewImport, request => sessionImportService.Preview(request));
        HandleAsync(ipcMain, HistoryIpc.CommitImport, request => sessionImportService.Commit(request));
        Handle(ipcMain, HistoryIpc.GetImportBatches, () => sessionImportService.GetBatches());
        HandleAsync(ipcMain, HistoryIpc.DeleteImportBatch, request => sessionImportService.DeleteBatch(request));

        // 统计数据集
        Handle(ipcMain, StatsIpc.GetDataset, request => statsDatasetService.Get(request, runningStatusJob.LastUpdateTime));

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
        HandleAsync(ipcMain, SteamLoginIpc.UpdateSavedTokenOptions, request =>
            loginService.UpdateSavedTokenOptions(request.Id, request.AutoLoginEnabled, request.LoginPersonaState));
        Handle(ipcMain, SteamLoginIpc.SetPersonaState, request =>
            loginService.SetUserPersonaState(request.AccountName, request.PersonaState));

        // Steam 好友
        HandleAsync(ipcMain, SteamFriendsIpc.GetSnapshot, async () =>
            IpcDtoMapper.ToDto(await friendsService.GetFriendsSnapshotAsync()));
        HandleAsync(ipcMain, SteamFriendsIpc.Refresh, async () =>
            IpcDtoMapper.ToDto(await friendsService.RefreshFriendsAsync()));

        // 好友状态变化记录
        HandleAsync(ipcMain, SteamFriendsIpc.StartTracking, request =>
            friendStatusRecordService.StartTrackingAsync(request.AccountName, request.FriendSteamIds));
        HandleAsync(ipcMain, SteamFriendsIpc.StopTracking, request =>
            friendStatusRecordService.StopTrackingAsync(request.AccountName, request.FriendSteamIds));
        Handle(ipcMain, SteamFriendsIpc.GetTracking, request =>
            friendStatusRecordService.GetTrackedFriends(request.AccountName));
        Handle(ipcMain, SteamFriendsIpc.GetRecords, request =>
            friendStatusRecordService.GetRecords(request).Select(IpcDtoMapper.ToDto).ToArray());
        HandleAsync(ipcMain, SteamFriendsIpc.ClearRecords, request =>
            friendStatusRecordService.ClearRecordsAsync(request));

        // 好友对比（游戏库 / 成就）
        HandleAsync(ipcMain, SteamFriendsIpc.GetLibrary, async request => IpcDtoMapper.ToDto(
            request.FriendSteamId,
            await friendCompareService.GetLibraryAsync(request.AccountName, request.FriendSteamId)));
        HandleAsync(ipcMain, SteamFriendsIpc.GetAchievements, async request => IpcDtoMapper.ToDto(
            request.FriendSteamId,
            request.AppId,
            await friendCompareService.GetAchievementUnlocksAsync(request.AccountName, request.FriendSteamId, request.AppId)));

        // Steam 游戏库
        HandleAsync(ipcMain, SteamLibraryIpc.GetSnapshot, async () =>
            IpcDtoMapper.ToDto(await libraryService.GetLibrarySnapshotAsync()));
        HandleAsync(ipcMain, SteamLibraryIpc.Refresh, async () =>
            IpcDtoMapper.ToDto(await libraryService.RefreshLibraryAsync()));
        HandleAsync(ipcMain, SteamLibraryIpc.GetPage, async request =>
            IpcDtoMapper.ToDto(await libraryService.GetLibraryPageAsync(
                request.AccountName, request.Offset, request.Limit)));

        HandleAsync(ipcMain, AchievementIpc.GetOverview, async request =>
            IpcDtoMapper.ToDto(await achievementsService.GetOverviewAsync(request.AccountName)));
        HandleAsync(ipcMain, AchievementIpc.GetGame, async request =>
            IpcDtoMapper.ToDto(await achievementsService.GetGameAsync(request.AccountName, request.AppId)));
        HandleAsync(ipcMain, AchievementIpc.RefreshGame, async request =>
            IpcDtoMapper.ToDto(await achievementsService.RefreshGameAsync(request.AccountName, request.AppId)));

        #endregion

        #region Job 相关 API

        Handle(ipcMain, JobIpc.GetUpdateAppRunningStatus, () => runningStatusJob.GetStatus());

        #endregion

        #region Setting 相关 API

        Handle(ipcMain, SettingIpc.Get, () => IpcDtoMapper.ToDto(settingsCoordinator.GetSettings()));
        HandleAsync(ipcMain, SettingIpc.Update, request => settingsCoordinator.UpdateSettingsAsync(IpcDtoMapper.ToCore(request)));

        #endregion

        #region Updater 相关 API

        HandleAsync(ipcMain, UpdaterIpc.GetStatus, () => updateService.GetStatus());
        On(ipcMain, UpdaterIpc.Check, () => updateService.CheckForUpdate());
        On(ipcMain, UpdaterIpc.Download, () => updateService.DownloadUpdate());
        On(ipcMain, UpdaterIpc.QuitAndInstall, () => updateService.QuitAndInstall());

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

        logger.LogInformation("IPC handlers registered");
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
