using ElectronNet.Persistence;
using ElectronNET.API;
using ElectronNET.Runtime.Data;
using ElectronNet.Features.Diagnostics;
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
    SteamAppImagesService appImagesService,
    UseAppRecordService useAppRecordService,
    UpdateService updateService,
    SteamLoginService loginService,
    SteamLibraryService libraryService,
    SteamAchievementsService achievementsService,
    AchievementArchiveService achievementArchiveService,
    LibraryArchiveService libraryArchiveService,
    ScreenshotArchiveService screenshotArchiveService,
    SaveBackupService saveBackupService,
    SaveAnalysisService saveAnalysisService,
    ProfileHistoryService profileHistoryService,
    DataExportService dataExportService,
    BackupService backupService,
    SyncService syncService,
    PublicDataService publicDataService,
    TaskProgressPublisher taskProgress,
    SteamFriendsService friendsService,
    SteamFriendCompareService friendCompareService,
    SteamOperationalStatusService operationalStatusService,
    FriendStatusRecordService friendStatusRecordService,
    LocalDataSourceService localDataSourceService,
    SessionManagementService sessionManagementService,
    PlaytimeHistoryService playtimeHistoryService,
    SessionImportService sessionImportService,
    StatsDatasetService statsDatasetService,
    SemanticLayerService semanticLayer,
    ArchiveHealthService archiveHealthService,
    StatsExplainService statsExplainService,
    ArchiveSealService archiveSealService,
    InsightService insightService,
    WeeklyService weeklyService,
    MemoirService memoirService,
    MemoirJournalService memoirJournal,
    StaticSiteService staticSite,
    ElectronNet.Services.Collectors.ArchiveCollectionService archiveCollection,
    ElectronNet.Services.Collectors.WebArchiveReparser webArchiveReparser,
    ArchiveQueryService archiveQuery,
    CircleService circle,
    ExchangeCardService exchangeCards,
    SimulatorService simulator,
    StoreRegionService storeRegions,
    PosterExportService posterExport,
    DatabaseSnapshotter databaseSnapshotter,
    SettingsCoordinator settingsCoordinator,
    SteamStat.Core.Http.SteamNetworkDiagnostics networkDiagnostics,
    UpdateAppRunningStatusJob runningStatusJob,
    IpcRequestBinder requestBinder,
    IpcFailurePolicy failures,
    ShellIpcPolicy shellPolicy,
    TrayService trayService,
    DesktopNotificationService notificationService,
    DiagnosticsService diagnosticsService,
    CrashReportService crashReportService,
    PopoutWindowService popoutWindows,
    AppearanceService appearanceService,
    IEventBus eventBus,
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
        HandleAsync(ipcMain, SteamIpc.RunAppAction, RunAppActionAsync);
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
        HandleAsync(ipcMain, SteamIpc.GetAppImages, request => appImagesService.GetAsync(request));
        HandleAsync(ipcMain, SteamIpc.RefreshAppsInfo, async request =>
            (IReadOnlyList<SteamAppDto>)(await steamAppService.SyncAndGetAllWithQuery(request)).Select(IpcDtoMapper.ToDto).ToArray());

        // Steam 使用统计
        Handle(ipcMain, SteamIpc.GetValidUseAppRecords, request => new UseAppRecordsDto(
            useAppRecordService.GetValidByParam(request),
            runningStatusJob.LastUpdateTime));
        Handle(ipcMain, SteamIpc.GetCurrentSessions, () => useAppRecordService.GetOngoing());
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
        HandleAsync(ipcMain, HistoryIpc.CommitImport, request => sessionImportService.Commit(request, taskProgress.For("historyImport")));
        Handle(ipcMain, HistoryIpc.GetImportBatches, () => sessionImportService.GetBatches());
        HandleAsync(ipcMain, HistoryIpc.DeleteImportBatch, request => sessionImportService.DeleteBatch(request));

        // 统计数据集
        Handle(ipcMain, StatsIpc.GetDataset, request => statsDatasetService.Get(request, runningStatusJob.LastUpdateTime));
        Handle(ipcMain, StatsIpc.GetDatasetRevision, () => statsDatasetService.GetRevision(
            System.Text.Json.JsonSerializer.Serialize(settingsCoordinator.GetSettings().SessionTracking)));
        HandleAsync(ipcMain, SemanticIpc.GetCatalog, () => semanticLayer.GetCatalogAsync());
        HandleAsync(ipcMain, SemanticIpc.Query, request => semanticLayer.QueryForIpcAsync(request));
        HandleAsync(ipcMain, InsightIpc.GetReport, request => insightService.GetReportAsync(request));
        HandleAsync(ipcMain, WeeklyIpc.GetIssues, request => weeklyService.ListAsync(request));
        HandleAsync(ipcMain, WeeklyIpc.GetIssue, request => weeklyService.GetAsync(request.Id));
        HandleAsync(ipcMain, WeeklyIpc.MarkRead, request => weeklyService.MarkReadAsync(request.Id));
        HandleAsync(ipcMain, WeeklyIpc.Backfill, request => weeklyService.BackfillAsync(request.AnchorTime));
        HandleAsync(ipcMain, MemoirIpc.GetTimeMachine, request => memoirService.GetTimeMachineAsync(request));
        HandleAsync(ipcMain, MemoirIpc.GetChapters, request => memoirService.GetChaptersAsync(request));
        HandleAsync(ipcMain, MemoirIpc.GetPrehistory, request => memoirService.GetPrehistoryAsync(request));
        HandleAsync(ipcMain, MemoirIpc.GetLifeEvents, () => memoirJournal.GetLifeEventsAsync());
        HandleAsync(ipcMain, MemoirIpc.SaveLifeEvent, request => memoirJournal.SaveLifeEventAsync(request));
        HandleAsync(ipcMain, MemoirIpc.DeleteLifeEvent, request => memoirJournal.DeleteLifeEventAsync(request.Id));
        HandleAsync(ipcMain, MemoirIpc.GetCapsules, () => memoirJournal.GetCapsulesAsync());
        HandleAsync(ipcMain, MemoirIpc.CreateCapsule, request => memoirJournal.CreateCapsuleAsync(request));
        HandleAsync(ipcMain, MemoirIpc.OpenCapsule, request => memoirJournal.OpenCapsuleAsync(request.Id));
        HandleAsync(ipcMain, MemoirIpc.DeleteCapsule, request => memoirJournal.DeleteCapsuleAsync(request.Id));
        HandleAsync(ipcMain, MemoirIpc.GetPreviouslyOn, request => memoirJournal.GetPreviouslyOnAsync(request));
        HandleAsync(ipcMain, MemoirIpc.PreviewSite, request => staticSite.PreviewAsync(request));
        HandleAsync(ipcMain, MemoirIpc.ExportSite, request => staticSite.ExportAsync(request));
        HandleAsync(ipcMain, MemoirIpc.RevealSite, request => staticSite.RevealAsync(request.ExportId));
        HandleAsync(ipcMain, ArchiveCollectionIpc.GetCollectors, () => archiveCollection.GetStatusAsync());
        HandleAsync(ipcMain, ArchiveCollectionIpc.RunCollector, request => archiveCollection.RunNowAsync(request.Id));
        HandleAsync(ipcMain, ArchiveCollectionIpc.GetWebCaptures, () => archiveQuery.GetWebCapturesAsync());
        HandleAsync(ipcMain, ArchiveCollectionIpc.ReparseWebCaptures, async () =>
        {
            await archiveQuery.MarkLayoutFailuresForReparseAsync();
            return await webArchiveReparser.RunIfNeededAsync();
        });
        HandleAsync(ipcMain, GameArchiveIpc.GetGame, request => archiveQuery.GetGameAsync(request));
        HandleAsync(ipcMain, GameArchiveIpc.GetPlayerCurve, request => archiveQuery.GetPlayerCurveAsync(request));
        HandleAsync(ipcMain, GameArchiveIpc.GetRichPresence, request => archiveQuery.GetRichPresenceAsync(request));
        HandleAsync(ipcMain, GameArchiveIpc.GetPlatforms, request => archiveQuery.GetPlatformsAsync(request));
        HandleAsync(ipcMain, GameArchiveIpc.GetPioneer, request => archiveQuery.GetPioneerAsync(request));
        HandleAsync(ipcMain, SpendingIpc.GetPurchases, request => archiveQuery.GetPurchasesAsync(request));
        HandleAsync(ipcMain, SpendingIpc.GetWishlist, request => archiveQuery.GetWishlistAsync(request));
        HandleAsync(ipcMain, SpendingIpc.SearchSimulator, request => simulator.SearchAsync(request));
        HandleAsync(ipcMain, SpendingIpc.RunSimulator, request => simulator.RunAsync(request));
        HandleAsync(ipcMain, SpendingIpc.GetStoreRegion, request => storeRegions.ResolveAsync(request.SteamIds));
        HandleAsync(ipcMain, ArtifactIpc.GetOverview, () => archiveQuery.GetArtifactsAsync());
        HandleAsync(ipcMain, CommunityArchiveIpc.GetCommunity, request => archiveQuery.GetCommunityAsync(request));
        HandleAsync(ipcMain, CommunityArchiveIpc.GetComments, request => archiveQuery.GetCommentsAsync(request));
        HandleAsync(ipcMain, CommunityArchiveIpc.GetFamily, () => archiveQuery.GetFamilyAsync());
        HandleAsync(ipcMain, CommunityArchiveIpc.GetFriendPlaytime, request => archiveQuery.GetFriendPlaytimeAsync(request));

        // 好友与圈子
        HandleAsync(ipcMain, CircleIpc.GetTogether, request => circle.GetTogetherAsync(request));
        HandleAsync(ipcMain, CircleIpc.GetFriendship, request => circle.GetFriendshipAsync(request));
        HandleAsync(ipcMain, CircleIpc.GetCompatibility, request => circle.GetCompatibilityAsync(request));
        HandleAsync(ipcMain, CircleIpc.PlanParty, request => circle.PlanPartyAsync(request));
        HandleAsync(ipcMain, CircleIpc.GetLeaderboard, request => circle.GetLeaderboardAsync(request));
        HandleAsync(ipcMain, CircleIpc.GetAdoption, request => circle.GetAdoptionAsync(request));
        HandleAsync(ipcMain, CircleIpc.GetLibraryEvents, request => circle.GetLibraryEventsAsync(request));
        HandleAsync(ipcMain, CircleIpc.GetAchievementRace, request => circle.GetAchievementRaceAsync(request));
        HandleAsync(ipcMain, CircleIpc.ExportCard, request => exchangeCards.ExportAsync(request));
        HandleAsync(ipcMain, CircleIpc.SaveCardFile, request => exchangeCards.SaveFileAsync(request));
        HandleAsync(ipcMain, CircleIpc.OpenCardFile, () => exchangeCards.OpenFileAsync());
        HandleAsync(ipcMain, CircleIpc.ImportCard, request => exchangeCards.ImportAsync(request));
        HandleAsync(ipcMain, CircleIpc.GetCards, () => exchangeCards.GetCardsAsync());
        HandleAsync(ipcMain, CircleIpc.DeleteCard, request => exchangeCards.DeleteAsync(request));
        HandleAsync(ipcMain, CircleIpc.GetDuoReview, request => exchangeCards.GetDuoReviewAsync(request));
        HandleAsync(ipcMain, RecordingIpc.GetRecordings, request => archiveQuery.GetRecordingsAsync(request));
        HandleAsync(ipcMain, RecordingIpc.Reveal, async request =>
        {
            var folder = await archiveQuery.GetRecordingFolderAsync(request.Id);
            if (folder == null) return false;
            await ElectronNET.API.Electron.Shell.ShowItemInFolderAsync(folder);
            return true;
        });
        HandleAsync(ipcMain, SpectrumIpc.GetPending, request => archiveQuery.GetPalettePendingAsync(request));
        HandleAsync(ipcMain, SpectrumIpc.GetImage, request => archiveQuery.GetPaletteImageAsync(request.Id));
        HandleAsync(ipcMain, SpectrumIpc.Save, request => archiveQuery.SavePalettesAsync(request));
        HandleAsync(ipcMain, SpectrumIpc.GetSpectrum, request => archiveQuery.GetSpectrumAsync(request));
        HandleAsync(ipcMain, SpectrumIpc.SavePoster, request => posterExport.SaveAsync(request));
        HandleAsync(ipcMain, ReliabilityIpc.RunHealthCheck, () => archiveHealthService.RunAsync());
        HandleAsync(ipcMain, ReliabilityIpc.Explain, request => statsExplainService.ExplainAsync(request));
        Handle(ipcMain, ReliabilityIpc.GetSnapshots, () => databaseSnapshotter.List());
        HandleAsync(ipcMain, ReliabilityIpc.RestoreSnapshot, async request =>
            databaseSnapshotter.Resolve(request.Name) is { } file
                ? await backupService.StageSnapshotRestoreAsync(file)
                : new BackupRestoreResultDto { Success = false, Error = "invalid" });
        HandleAsync(ipcMain, ReliabilityIpc.GetSeals, () => archiveSealService.GetOverviewAsync());
        HandleAsync(ipcMain, ReliabilityIpc.CreateSeal, request => archiveSealService.CreateAsync(request.Year));
        HandleAsync(ipcMain, ReliabilityIpc.RevealSeal, request => archiveSealService.RevealAsync(request.Year));
        Handle(ipcMain, ReliabilityIpc.GetRichPresenceCoverage, () => IpcDtoMapper.ToRichPresenceCoverage(friendsService));

        // Steam 登录
        HandleAsync(ipcMain, SteamLoginIpc.StartCredentials, async request => IpcDtoMapper.ToDto(
            await loginService.LoginWithCredentials(request.Username, request.Password, request.RememberMe, request.PersonaState)));
        HandleAsync(ipcMain, SteamLoginIpc.StartQr, async request =>
            IpcDtoMapper.ToDto(await loginService.LoginWithQR(request.RememberMe, request.PersonaState)));
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
        HandleAsync(ipcMain, SteamFriendsIpc.StartTracking, async request =>
        {
            var result = await friendStatusRecordService.StartTrackingAsync(request.AccountName, request.FriendSteamIds);
            circle.Invalidate();
            return result;
        });
        HandleAsync(ipcMain, SteamFriendsIpc.StopTracking, async request =>
        {
            var result = await friendStatusRecordService.StopTrackingAsync(request.AccountName, request.FriendSteamIds);
            circle.Invalidate();
            return result;
        });
        Handle(ipcMain, SteamFriendsIpc.GetTracking, request =>
            friendStatusRecordService.GetTrackedFriends(request.AccountName));
        Handle(ipcMain, SteamFriendsIpc.GetRecords, request =>
            friendStatusRecordService.GetRecords(request).Select(IpcDtoMapper.ToDto).ToArray());
        HandleAsync(ipcMain, SteamFriendsIpc.GetObservationWindows, request => friendStatusRecordService.GetObservationWindowsAsync(request));
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
            IpcDtoMapper.ToDto(await libraryService.RefreshLibraryAsync(taskProgress.For("librarySync"))));
        On(ipcMain, SteamLibraryIpc.CancelRefresh, () => libraryService.CancelRefresh());
        HandleAsync(ipcMain, SteamLibraryIpc.GetPage, async request =>
            IpcDtoMapper.ToDto(await libraryService.GetLibraryPageAsync(
                request.AccountName, request.Offset, request.Limit)));

        HandleAsync(ipcMain, AchievementIpc.GetOverview, async request =>
            IpcDtoMapper.ToDto(await achievementsService.GetOverviewAsync(request.AccountName)));
        // 单游戏成就详情：返回前顺带写入成就档案
        HandleAsync(ipcMain, AchievementIpc.GetGame, async request =>
            IpcDtoMapper.ToDto(await achievementArchiveService.CaptureAsync(request.AccountName,
                await achievementsService.GetGameAsync(request.AccountName, request.AppId))));
        HandleAsync(ipcMain, AchievementIpc.RefreshGame, async request =>
            IpcDtoMapper.ToDto(await achievementArchiveService.CaptureAsync(request.AccountName,
                await achievementsService.RefreshGameAsync(request.AccountName, request.AppId))));

        // 成就档案（时间线 / 完成度 / 目标清单）
        Handle(ipcMain, AchievementArchiveIpc.GetUnlocks, request => achievementArchiveService.GetUnlocks(request));
        Handle(ipcMain, AchievementArchiveIpc.GetRecentUnlocks, request => achievementArchiveService.GetRecentUnlocks(request));
        Handle(ipcMain, AchievementArchiveIpc.GetGames, request => achievementArchiveService.GetGames(request.AccountName));
        Handle(ipcMain, AchievementArchiveIpc.GetSets, request => achievementArchiveService.GetSets(request));
        Handle(ipcMain, AchievementArchiveIpc.GetLedger, request => achievementArchiveService.GetLedger(request));
        Handle(ipcMain, AchievementArchiveIpc.GetChanges, request => achievementArchiveService.GetChanges(request));
        Handle(ipcMain, AchievementArchiveIpc.StartSync, request =>
            achievementArchiveService.StartSync(request.AccountName, request.Force ?? false));
        On(ipcMain, AchievementArchiveIpc.CancelSync, () => achievementArchiveService.CancelSync());
        Handle(ipcMain, AchievementArchiveIpc.GetSyncStatus, () => achievementArchiveService.GetSyncStatus());
        Handle(ipcMain, AchievementArchiveIpc.GetTargets, request => achievementArchiveService.GetTargets(request.AccountName));
        HandleAsync(ipcMain, AchievementArchiveIpc.AddTarget, request => achievementArchiveService.AddTargetAsync(request));
        HandleAsync(ipcMain, AchievementArchiveIpc.RemoveTarget, request => achievementArchiveService.RemoveTargetAsync(request.Id));

        // 游戏库档案（入库时间 / 状态 / 评分 / 价格）
        HandleAsync(ipcMain, LibraryArchiveIpc.RefreshAcquisitions, request =>
            libraryArchiveService.RefreshAcquisitionsAsync(request.AccountName));
        Handle(ipcMain, LibraryArchiveIpc.GetAcquisitions, request => libraryArchiveService.GetAcquisitions(request));
        Handle(ipcMain, LibraryArchiveIpc.GetGameMeta, request =>
            libraryArchiveService.GetGameMeta(request.AccountName, request.Offset ?? 0, request.Limit ?? 100));
        HandleAsync(ipcMain, LibraryArchiveIpc.UpdateGameMeta, request => libraryArchiveService.UpdateGameMetaAsync(request));

        // 截图档案
        HandleAsync(ipcMain, ScreenshotIpc.RefreshIndex, () => screenshotArchiveService.RefreshAsync(taskProgress.For("screenshotIndex")));
        Handle(ipcMain, ScreenshotIpc.GetList, request => screenshotArchiveService.GetList(request));
        Handle(ipcMain, ScreenshotIpc.GetApps, () => screenshotArchiveService.GetApps());
        HandleAsync(ipcMain, ScreenshotIpc.RunAction, request => screenshotArchiveService.RunActionAsync(request));

        // 存档备份（只读快照，绝不写回）
        Handle(ipcMain, SaveBackupIpc.GetTargets, () => saveBackupService.GetTargets());
        HandleAsync(ipcMain, SaveBackupIpc.DiscoverTargets, () => saveBackupService.DiscoverAsync());
        HandleAsync(ipcMain, SaveBackupIpc.AddTarget, request => saveBackupService.AddTargetAsync(request));
        HandleAsync(ipcMain, SaveBackupIpc.PickDirectory, () => saveBackupService.PickDirectoryAsync());
        HandleAsync(ipcMain, SaveBackupIpc.UpdateTarget, request => saveBackupService.UpdateTargetAsync(request));
        HandleAsync(ipcMain, SaveBackupIpc.RemoveTarget, request => saveBackupService.RemoveTargetAsync(request));
        HandleAsync(ipcMain, SaveBackupIpc.OpenTarget, request => saveBackupService.OpenTargetAsync(request.TargetId));
        HandleAsync(ipcMain, SaveBackupIpc.Run, request => saveBackupService.RunAsync(request));
        On(ipcMain, SaveBackupIpc.Cancel, () => saveBackupService.Cancel());
        Handle(ipcMain, SaveBackupIpc.GetStatus, () => saveBackupService.GetStatus());
        Handle(ipcMain, SaveBackupIpc.GetSnapshots, request => saveBackupService.GetSnapshots(request.TargetId));
        Handle(ipcMain, SaveBackupIpc.GetSnapshotFiles, request => saveBackupService.GetSnapshotFiles(request.SnapshotId));
        HandleAsync(ipcMain, SaveBackupIpc.DeleteSnapshot, request => saveBackupService.DeleteSnapshotAsync(request.SnapshotId));
        HandleAsync(ipcMain, SaveBackupIpc.ExportSnapshot, request => saveBackupService.ExportSnapshotAsync(request.SnapshotId));

        // 存档分析插件（解析在渲染层沙箱中完成，Host 只负责插件管理与只读读取）
        Handle(ipcMain, SaveAnalysisIpc.GetPlugins, () => saveAnalysisService.GetPlugins());
        Handle(ipcMain, SaveAnalysisIpc.SetPluginEnabled, request => saveAnalysisService.SetEnabled(request));
        HandleAsync(ipcMain, SaveAnalysisIpc.UninstallPlugin, request => saveAnalysisService.UninstallAsync(request.PluginId));
        HandleAsync(ipcMain, SaveAnalysisIpc.OpenPluginsFolder, () => saveAnalysisService.OpenFolderAsync());
        Handle(ipcMain, SaveAnalysisIpc.GetPluginFile, request => saveAnalysisService.GetPluginFile(request));
        HandleAsync(ipcMain, SaveAnalysisIpc.GetRepository, () => saveAnalysisService.GetRepositoryAsync());
        HandleAsync(ipcMain, SaveAnalysisIpc.InstallPlugin, request => saveAnalysisService.InstallAsync(request.PluginId));
        HandleAsync(ipcMain, SaveAnalysisIpc.LocateSaves, request => saveAnalysisService.LocateAsync(request.PluginId));
        HandleAsync(ipcMain, SaveAnalysisIpc.PickSave, () => saveAnalysisService.PickAsync());
        HandleAsync(ipcMain, SaveAnalysisIpc.ReadSave, request => saveAnalysisService.ReadAsync(request));

        // 自己的资料变化
        Handle(ipcMain, ProfileHistoryIpc.Get, request => profileHistoryService.Get(request));
        HandleAsync(ipcMain, ProfileHistoryIpc.Capture, () => profileHistoryService.CaptureAsync());

        // 数据导出
        HandleAsync(ipcMain, DataExportIpc.Run, request => dataExportService.ExportAsync(request, taskProgress.For("dataExport")));
        HandleAsync(ipcMain, DataExportIpc.Reveal, request => dataExportService.RevealAsync(request.ExportId));

        // 手动备份与恢复
        HandleAsync(ipcMain, BackupIpc.Create, request => backupService.CreateAsync(request, taskProgress.For("backup")));
        On(ipcMain, BackupIpc.CancelCreate, () => backupService.CancelCreate());
        HandleAsync(ipcMain, BackupIpc.PickRestore, () => backupService.PickRestoreAsync());
        HandleAsync(ipcMain, BackupIpc.Unlock, request => backupService.UnlockAsync(request));
        HandleAsync(ipcMain, BackupIpc.StageRestore, request => backupService.StageRestoreAsync(request));
        Handle(ipcMain, BackupIpc.CancelRestore, () => backupService.CancelPendingRestore());
        Handle(ipcMain, BackupIpc.GetRestoreStatus, () => backupService.GetRestoreStatus());
        Handle(ipcMain, BackupIpc.TakeRendererState, () => backupService.TakeRendererState());

        // 多设备同步
        HandleAsync(ipcMain, SyncIpc.GetStatus, () => syncService.GetStatusAsync());
        HandleAsync(ipcMain, SyncIpc.Test, request => syncService.TestAsync(request));
        HandleAsync(ipcMain, SyncIpc.Setup, request => syncService.SetupAsync(request));
        Handle(ipcMain, SyncIpc.Run, () => syncService.RunNow());
        On(ipcMain, SyncIpc.Cancel, () => syncService.Cancel());
        HandleAsync(ipcMain, SyncIpc.Disable, request => syncService.DisableAsync(request));
        HandleAsync(ipcMain, SyncIpc.ChangePassphrase, request => syncService.ChangePassphraseAsync(request));
        HandleAsync(ipcMain, SyncIpc.PickFolder, () => syncService.PickFolderAsync());

        // 公共数据层
        Handle(ipcMain, PublicDataIpc.GetStatus, () => publicDataService.GetStatus());
        HandleAsync(ipcMain, PublicDataIpc.Refresh, () => publicDataService.RefreshAsync(taskProgress.For("publicData")));
        HandleAsync(ipcMain, PublicDataIpc.SetEnabled, request => publicDataService.SetEnabledAsync(request.Enabled));

        #endregion

        #region Job 相关 API

        Handle(ipcMain, JobIpc.GetUpdateAppRunningStatus, () => runningStatusJob.GetStatus());

        #endregion

        #region Setting 相关 API

        Handle(ipcMain, SettingIpc.Get, () => IpcDtoMapper.ToDto(settingsCoordinator.GetSettings()));
        HandleAsync(ipcMain, SettingIpc.Update, async request =>
        {
            // 非空的自定义代理地址必须合法（scheme://host:port），否则整笔拒绝，避免写入后静默退回系统代理
            if (!string.IsNullOrWhiteSpace(request.Network?.ProxyUrl)
                && !SteamStat.Core.Http.SteamNetworkProfile.TryParseProxyUri(request.Network.ProxyUrl, out _, out var proxyError))
            {
                logger.LogWarning("Rejected settings update with invalid proxy URL ({Reason})", proxyError);
                return false;
            }
            var updated = await settingsCoordinator.UpdateSettingsAsync(IpcDtoMapper.ToCore(request));
            if (updated) await eventBus.PublishAsync(new AppSettingsChanged(IpcDtoMapper.ToDto(settingsCoordinator.GetSettings())));
            return updated;
        });

        #endregion

        #region Network 相关 API

        HandleAsync(ipcMain, NetworkIpc.RunDiagnostics, async () =>
            IpcDtoMapper.ToDto(await networkDiagnostics.RunAsync()));

        #endregion

        #region Updater 相关 API

        HandleAsync(ipcMain, UpdaterIpc.GetStatus, () => updateService.GetStatus());
        On(ipcMain, UpdaterIpc.Check, () => updateService.CheckForUpdate());
        On(ipcMain, UpdaterIpc.Download, () => updateService.DownloadUpdate());
        On(ipcMain, UpdaterIpc.QuitAndInstall, () => updateService.QuitAndInstall());

        #endregion

        #region 反馈与诊断 API

        HandleAsync(ipcMain, AppInfoIpc.Get, () => diagnosticsService.GetAppInfoAsync());
        HandleAsync(ipcMain, DiagnosticsIpc.CreateBundle, request => diagnosticsService.CreateBundleAsync(request));
        On(ipcMain, DiagnosticsIpc.RevealBundle, request => _ = diagnosticsService.RevealBundleAsync(request.FileName));
        HandleAsync(ipcMain, DiagnosticsIpc.OpenIssue, request => diagnosticsService.OpenIssueAsync(request));
        Handle(ipcMain, CrashReportIpc.List, () => new CrashReportListDto
        {
            Enabled = crashReportService.Enabled,
            Sink = crashReportService.SinkId,
            RequiresUserAction = crashReportService.RequiresUserAction,
            AutoUpload = crashReportService.AutoUpload,
            Reports = crashReportService.List().Select(IpcDtoMapper.ToDto).ToArray()
        });
        HandleAsync(ipcMain, CrashReportIpc.Submit, async request =>
        {
            var submission = await crashReportService.SubmitAsync(request.Id, CancellationToken.None);
            return new CrashReportSubmitResultDto { Success = submission.Success, Url = submission.Url };
        });
        Handle(ipcMain, CrashReportIpc.Dismiss, request => crashReportService.Dismiss(request.Id));
        On(ipcMain, CrashReportIpc.Record, request => crashReportService.RecordRenderer(request));

        #endregion

        #region App & Window 相关 API

        On(ipcMain, AppWindowIpc.Quit, () => app.Quit());
        HandleAsync(ipcMain, AppWindowIpc.Relaunch, () => RelaunchAsync(app));

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

        HandleAsync(ipcMain, PopoutIpc.Open, request => popoutWindows.OpenAsync(request.Path));
        HandleAsync(ipcMain, PopoutIpc.Control, request => popoutWindows.ControlAsync(request.Id, request.Action));
        HandleAsync(ipcMain, AppearanceIpc.PickBackground, () => appearanceService.PickBackgroundAsync());

        #endregion

        #region Tray & Notification 相关 API

        On(ipcMain, TrayIpc.UpdateState, request => trayService.UpdateState(request));
        On(ipcMain, NotificationIpc.Show, request => notificationService.Show(request));

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

    /// <summary>
    /// 重启应用（恢复备份后使用）：先以当前时间结束进行中的记录，再退出。
    /// Electron 先启动的运行方式（发行包）由 Electron 在退出后重新拉起；.NET 先启动的开发模式只退出。
    /// </summary>
    private async Task<AppRelaunchResultDto> RelaunchAsync(App app)
    {
        try
        {
            await runningStatusJob.EndAllRecordingsAsync(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to end recordings before relaunching");
        }
        var canRelaunch = ElectronNET.ElectronNetRuntime.StartupMethod is StartupMethod.PackagedElectronFirst or StartupMethod.UnpackedElectronFirst;
        if (canRelaunch) app.Relaunch();
        _ = Task.Run(async () =>
        {
            // 先让 IPC 回复送达渲染层
            await Task.Delay(300);
            app.Quit();
        });
        return new AppRelaunchResultDto { Relaunched = canRelaunch };
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

    /// <summary>
    /// 通过 steam:// 协议把操作交给 Steam 客户端执行（URL 由 Host 拼接，渲染层只传 AppId 与动作）
    /// </summary>
    private async Task<bool> RunAppActionAsync(SteamAppActionRequest request)
    {
        var url = ShellIpcPolicy.SteamAppActionUrl(request.Action, request.AppId);
        if (url == null) return false;
        try
        {
            await Electron.Shell.OpenExternalAsync(url).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to hand a Steam app action to the Steam client");
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

    private void Handle<TResponse>(
        IpcMain ipcMain,
        IpcInvoke<IpcNoRequest, TResponse> endpoint,
        Func<TResponse> handler)
        => ipcMain.Handle(endpoint.Channel, _ => failures.Run(endpoint, () => handler()!));

    private void Handle<TRequest, TResponse>(
        IpcMain ipcMain,
        IpcInvoke<TRequest, TResponse> endpoint,
        Func<TRequest, TResponse> handler)
        => ipcMain.Handle(endpoint.Channel, value => failures.Run(endpoint, () => handler(requestBinder.Bind<TRequest>(value, endpoint))!));

    private void HandleAsync<TResponse>(
        IpcMain ipcMain,
        IpcInvoke<IpcNoRequest, TResponse> endpoint,
        Func<Task<TResponse>> handler)
        => ipcMain.Handle(endpoint.Channel, _ => failures.RunAsync(endpoint, async () => (object)(await handler())!));

    private void HandleAsync<TRequest, TResponse>(
        IpcMain ipcMain,
        IpcInvoke<TRequest, TResponse> endpoint,
        Func<TRequest, Task<TResponse>> handler)
        => ipcMain.Handle(endpoint.Channel, value => failures.RunAsync(endpoint, async () =>
            (object)(await handler(requestBinder.Bind<TRequest>(value, endpoint)))!));

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
