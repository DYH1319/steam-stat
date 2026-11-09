namespace SteamStat.Contracts.Ipc;

public enum IpcDirection
{
    Invoke,
    Send,
    HostToRendererEvent
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class IpcTypeNameAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class IpcOptionalAttribute : Attribute;

[AttributeUsage(AttributeTargets.Property)]
public sealed class IpcNumberAttribute : Attribute;

[AttributeUsage(AttributeTargets.Property)]
public sealed class IpcMaxLengthAttribute(int length) : Attribute
{
    public int Length { get; } = length;
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class IpcRangeAttribute(double minimum, double maximum) : Attribute
{
    public double Minimum { get; } = minimum;
    public double Maximum { get; } = maximum;
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class IpcStringValuesAttribute(params string[] values) : Attribute
{
    public IReadOnlyList<string> Values { get; } = values;
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class)]
public sealed class IpcUnionAttribute(params Type[] types) : Attribute
{
    public IReadOnlyList<Type> Types { get; } = types;
}

public sealed class IpcNoRequest;
public sealed class IpcNoPayload;

public interface IIpcEndpointDescriptor
{
    string Channel { get; }
    string ApiMethod { get; }
    string? RemoveApiMethod { get; }
    IpcDirection Direction { get; }
    Type? RequestType { get; }
    Type? ResponseType { get; }
    bool AllowsEmptyRequest { get; }
    bool ResponseNullable { get; }
}

public abstract record IpcEndpointDescriptor(
    string Channel,
    string ApiMethod,
    IpcDirection Direction,
    Type? RequestType,
    Type? ResponseType,
    bool AllowsEmptyRequest,
    string? RemoveApiMethod = null,
    bool ResponseNullable = false) : IIpcEndpointDescriptor;

public sealed record IpcInvoke<TRequest, TResponse>(
    string Channel,
    string ApiMethod,
    bool AllowsEmptyRequest = false,
    bool IsResponseNullable = false) : IpcEndpointDescriptor(
        Channel,
        ApiMethod,
        IpcDirection.Invoke,
        typeof(TRequest) == typeof(IpcNoRequest) ? null : typeof(TRequest),
        typeof(TResponse),
        AllowsEmptyRequest || typeof(TRequest) == typeof(IpcNoRequest),
        ResponseNullable: IsResponseNullable);

public sealed record IpcSend<TRequest>(
    string Channel,
    string ApiMethod,
    bool AllowsEmptyRequest = false) : IpcEndpointDescriptor(
        Channel,
        ApiMethod,
        IpcDirection.Send,
        typeof(TRequest) == typeof(IpcNoRequest) ? null : typeof(TRequest),
        null,
        AllowsEmptyRequest || typeof(TRequest) == typeof(IpcNoRequest));

public sealed record IpcHostEvent<TPayload>(
    string Channel,
    string ApiMethod,
    string RemoveApiMethod) : IpcEndpointDescriptor(
        Channel,
        ApiMethod,
        IpcDirection.HostToRendererEvent,
        null,
        typeof(TPayload) == typeof(IpcNoPayload) ? null : typeof(TPayload),
        true,
        RemoveApiMethod);

public static class SteamIpc
{
    public static readonly IpcInvoke<IpcNoRequest, GlobalStatusDto?> GetStatus =
        new("steam:status:get", "steamGetStatus", IsResponseNullable: true);
    public static readonly IpcInvoke<IpcNoRequest, GlobalStatusDto?> RefreshStatus =
        new("steam:status:refresh", "steamRefreshStatus", IsResponseNullable: true);
    public static readonly IpcInvoke<IpcNoRequest, IReadOnlyList<string>> GetLibraryFolders =
        new("steam:libraryFolders:get", "steamGetLibraryFolders");
    public static readonly IpcInvoke<SteamAppActionRequest, bool> RunAppAction =
        new("steam:app:action", "steamAppAction");
    public static readonly IpcInvoke<IpcNoRequest, SteamOperationalStatusDto> GetOperationalStatus =
        new("steam:operationalStatus:get", "steamOperationalStatusGet");
    public static readonly IpcInvoke<IpcNoRequest, SteamLocalDataSourcesDto> GetLocalDataSources =
        new("steam:localDataSources:get", "steamLocalDataSourcesGet");
    public static readonly IpcInvoke<IpcNoRequest, IReadOnlyList<SteamUserDto>> GetLoginUsers =
        new("steam:loginUsers:get", "steamGetLoginUser");
    public static readonly IpcInvoke<IpcNoRequest, IReadOnlyList<SteamUserDto>> RefreshLoginUsers =
        new("steam:loginUsers:refresh", "steamRefreshLoginUser");
    public static readonly IpcInvoke<ChangeSteamUserRequest, bool> ChangeLoginUser =
        new("steam:loginUser:change", "steamChangeLoginUser");
    public static readonly IpcHostEvent<IpcNoPayload> LoginUsersUpdated =
        new("steam:loginUsers:updated", "steamUserUpdatedOnListener", "steamUserUpdatedRemoveListener");
    public static readonly IpcInvoke<IpcNoRequest, RunningAppsDto> GetRunningApps =
        new("steam:runningApps:get", "steamGetRunningApps");
    public static readonly IpcInvoke<SteamAppsQueryRequest, IReadOnlyList<SteamAppDto>> GetAppsInfo =
        new("steam:appsInfo:get", "steamGetAppsInfo", AllowsEmptyRequest: true);
    public static readonly IpcInvoke<SteamAppImagesRequest, IReadOnlyList<SteamAppImagesDto>> GetAppImages =
        new("steam:appImages:get", "steamAppImagesGet");
    public static readonly IpcInvoke<SteamAppsQueryRequest, IReadOnlyList<SteamAppDto>> RefreshAppsInfo =
        new("steam:appsInfo:refresh", "steamRefreshAppsInfo", AllowsEmptyRequest: true);
    public static readonly IpcInvoke<UseAppRecordsQueryRequest, UseAppRecordsDto> GetValidUseAppRecords =
        new("steam:validUseAppRecord:get", "steamGetValidUseAppRecord", AllowsEmptyRequest: true);
    public static readonly IpcInvoke<IpcNoRequest, IReadOnlyList<CurrentSessionDto>> GetCurrentSessions =
        new("steam:currentSessions:get", "steamCurrentSessionsGet");
    public static readonly IpcInvoke<IpcNoRequest, IReadOnlyList<SteamUserDto>> GetUsersInRecords =
        new("steam:usersInRecords:get", "steamGetUsersInRecord");
    public static readonly IpcInvoke<IpcNoRequest, bool> EndUseAppRecording =
        new("steam:useAppRecording:end", "steamEndUseAppRecording");
    public static readonly IpcInvoke<IpcNoRequest, bool> DiscardUseAppRecording =
        new("steam:useAppRecording:discard", "steamDiscardUseAppRecording");
}

public static class SteamLoginIpc
{
    public static readonly IpcInvoke<SteamLoginCredentialsRequest, SteamLoginResultDto> StartCredentials =
        new("steamLogin:credentials:start", "steamLoginCredentialsStart");
    public static readonly IpcInvoke<SteamLoginQrRequest, SteamLoginResultDto> StartQr =
        new("steamLogin:qr:start", "steamLoginQrStart");
    public static readonly IpcInvoke<SteamLoginTokenRequest, SteamLoginResultDto> StartToken =
        new("steamLogin:token:start", "steamLoginTokenStart");
    public static readonly IpcInvoke<SteamLoginGuardCodeRequest, bool> SubmitGuardCode =
        new("steamLogin:guardCode:submit", "steamLoginGuardCodeSubmit");
    public static readonly IpcSend<IpcNoRequest> SwitchToUseCode =
        new("steamLogin:switchToUseCode", "steamLoginSwitchToUseCode");
    public static readonly IpcSend<IpcNoRequest> ConfirmDevice =
        new("steamLogin:confirmDevice", "steamLoginConfirmDevice");
    public static readonly IpcSend<IpcNoRequest> Cancel =
        new("steamLogin:cancel", "steamLoginCancel");
    public static readonly IpcInvoke<IpcNoRequest, IReadOnlyList<string>> GetLoggedInUsers =
        new("steamLogin:loggedInUsers:get", "steamLoginLoggedInUsersGet");
    public static readonly IpcInvoke<AccountNameRequest, bool> LogoutUser =
        new("steamLogin:user:logout", "steamLoginUserLogout");
    public static readonly IpcInvoke<IpcNoRequest, IReadOnlyList<SteamLoginTokenDto>> GetSavedTokens =
        new("steamLogin:savedTokens:get", "steamLoginSavedTokensGet");
    public static readonly IpcInvoke<SteamLoginTokenDeleteRequest, bool> DeleteSavedToken =
        new("steamLogin:savedToken:delete", "steamLoginSavedTokenDelete");
    public static readonly IpcInvoke<SteamLoginTokenOptionsUpdateRequest, bool> UpdateSavedTokenOptions =
        new("steamLogin:savedTokenOptions:update", "steamLoginSavedTokenOptionsUpdate");
    public static readonly IpcInvoke<SteamPersonaStateRequest, bool> SetPersonaState =
        new("steamLogin:user:setPersonaState", "steamLoginUserSetPersonaState");
    public static readonly IpcHostEvent<SteamLoginEventDto> Event =
        new("steamLogin:event", "steamLoginEventOnListener", "steamLoginEventRemoveListener");
}

public static class SteamFriendsIpc
{
    public static readonly IpcInvoke<IpcNoRequest, SteamFriendsResultDto> GetSnapshot =
        new("steamFriends:snapshot:get", "steamFriendsSnapshotGet");
    public static readonly IpcInvoke<IpcNoRequest, SteamFriendsResultDto> Refresh =
        new("steamFriends:refresh", "steamFriendsRefresh");
    public static readonly IpcHostEvent<SteamFriendsUpdatedEventDto> Updated =
        new("steamFriends:update", "steamFriendsUpdateOnListener", "steamFriendsUpdateRemoveListener");
    public static readonly IpcInvoke<SteamFriendsTrackingRequest, bool> StartTracking =
        new("steamFriends:track:start", "steamFriendsTrackStart");
    public static readonly IpcInvoke<SteamFriendsTrackingRequest, bool> StopTracking =
        new("steamFriends:track:stop", "steamFriendsTrackStop");
    public static readonly IpcInvoke<AccountNameRequest, IReadOnlyList<string>> GetTracking =
        new("steamFriends:track:get", "steamFriendsTrackGet");
    public static readonly IpcInvoke<FriendStatusRecordsQueryRequest, IReadOnlyList<FriendStatusRecordDto>> GetRecords =
        new("steamFriends:records:get", "steamFriendsRecordsGet", AllowsEmptyRequest: true);
    public static readonly IpcInvoke<FriendStatusRecordsClearRequest, int> ClearRecords =
        new("steamFriends:records:clear", "steamFriendsRecordsClear", AllowsEmptyRequest: true);
    public static readonly IpcInvoke<SteamFriendCompareRequest, SteamFriendLibraryResultDto> GetLibrary =
        new("steamFriends:library:get", "steamFriendsLibraryGet");
    public static readonly IpcInvoke<SteamFriendAchievementsRequest, SteamFriendAchievementsResultDto> GetAchievements =
        new("steamFriends:achievements:get", "steamFriendsAchievementsGet");
}

public static class SteamLibraryIpc
{
    public static readonly IpcInvoke<IpcNoRequest, SteamLibraryResultDto> GetSnapshot =
        new("steamLibrary:snapshot:get", "steamLibrarySnapshotGet");
    public static readonly IpcInvoke<IpcNoRequest, SteamLibraryResultDto> Refresh =
        new("steamLibrary:refresh", "steamLibraryRefresh");
    public static readonly IpcSend<IpcNoRequest> CancelRefresh =
        new("steamLibrary:refresh:cancel", "steamLibraryRefreshCancel");
    public static readonly IpcInvoke<SteamLibraryPageRequest, SteamLibraryPageDto> GetPage =
        new("steamLibrary:page:get", "steamLibraryPageGet");
}

public static class AchievementIpc
{
    public static readonly IpcInvoke<SteamAchievementOverviewRequest, SteamAchievementOverviewResultDto> GetOverview =
        new("steamAchievements:overview:get", "steamAchievementsOverviewGet");
    public static readonly IpcInvoke<SteamAchievementGameRequest, SteamAchievementGameResultDto> GetGame =
        new("steamAchievements:game:get", "steamAchievementsGameGet");
    public static readonly IpcInvoke<SteamAchievementGameRequest, SteamAchievementGameResultDto> RefreshGame =
        new("steamAchievements:game:refresh", "steamAchievementsGameRefresh");
}

public static class AchievementArchiveIpc
{
    public static readonly IpcInvoke<AchievementArchiveQueryRequest, AchievementArchivePageDto> GetUnlocks =
        new("achievementArchive:unlocks:get", "achievementArchiveUnlocksGet");
    public static readonly IpcInvoke<AchievementArchiveRecentRequest, IReadOnlyList<AchievementArchiveUnlockDto>> GetRecentUnlocks =
        new("achievementArchive:unlocks:recent", "achievementArchiveRecentGet");
    public static readonly IpcInvoke<AccountNameRequest, IReadOnlyList<AchievementArchiveGameDto>> GetGames =
        new("achievementArchive:games:get", "achievementArchiveGamesGet");
    public static readonly IpcInvoke<AchievementArchiveQueryRequest, AchievementArchiveSetPageDto> GetSets =
        new("achievementArchive:sets:get", "achievementArchiveSetsGet");
    public static readonly IpcInvoke<AchievementLedgerRequest, AchievementLedgerPageDto> GetLedger =
        new("achievementArchive:ledger:get", "achievementArchiveLedgerGet");
    public static readonly IpcInvoke<AchievementArchiveQueryRequest, AchievementSchemaChangePageDto> GetChanges =
        new("achievementArchive:changes:get", "achievementArchiveChangesGet");
    public static readonly IpcInvoke<AchievementArchiveSyncRequest, bool> StartSync =
        new("achievementArchive:sync:start", "achievementArchiveSyncStart");
    public static readonly IpcSend<IpcNoRequest> CancelSync =
        new("achievementArchive:sync:cancel", "achievementArchiveSyncCancel");
    public static readonly IpcInvoke<IpcNoRequest, AchievementArchiveSyncStatusDto> GetSyncStatus =
        new("achievementArchive:sync:status", "achievementArchiveSyncStatusGet");
    public static readonly IpcHostEvent<AchievementArchiveSyncStatusDto> SyncProgress =
        new("achievementArchive:sync:progress", "achievementArchiveSyncOnListener", "achievementArchiveSyncRemoveListener");
    public static readonly IpcInvoke<AccountNameRequest, IReadOnlyList<AchievementTargetDto>> GetTargets =
        new("achievementTargets:get", "achievementTargetsGet");
    public static readonly IpcInvoke<AchievementTargetAddRequest, AchievementTargetDto> AddTarget =
        new("achievementTargets:add", "achievementTargetAdd");
    public static readonly IpcInvoke<AchievementTargetRemoveRequest, bool> RemoveTarget =
        new("achievementTargets:remove", "achievementTargetRemove");
}

public static class LibraryArchiveIpc
{
    public static readonly IpcInvoke<AccountNameRequest, LibraryAcquisitionRefreshResultDto> RefreshAcquisitions =
        new("libraryArchive:acquisitions:refresh", "libraryAcquisitionsRefresh");
    public static readonly IpcInvoke<LibraryAcquisitionPageRequest, LibraryAcquisitionPageDto> GetAcquisitions =
        new("libraryArchive:acquisitions:get", "libraryAcquisitionsGet");
    public static readonly IpcInvoke<LibraryGameMetaQueryRequest, IReadOnlyList<LibraryGameMetaDto>> GetGameMeta =
        new("libraryArchive:meta:get", "libraryGameMetaGet");
    public static readonly IpcInvoke<LibraryGameMetaUpdateRequest, int> UpdateGameMeta =
        new("libraryArchive:meta:update", "libraryGameMetaUpdate");
}

public static class ScreenshotIpc
{
    public static readonly IpcInvoke<IpcNoRequest, ScreenshotIndexResultDto> RefreshIndex =
        new("screenshot:index:refresh", "screenshotIndexRefresh");
    public static readonly IpcInvoke<ScreenshotQueryRequest, ScreenshotPageDto> GetList =
        new("screenshot:list:get", "screenshotListGet");
    public static readonly IpcInvoke<IpcNoRequest, IReadOnlyList<ScreenshotAppDto>> GetApps =
        new("screenshot:apps:get", "screenshotAppsGet");
    public static readonly IpcInvoke<ScreenshotActionRequest, bool> RunAction =
        new("screenshot:action", "screenshotAction");
}

public static class SaveBackupIpc
{
    public static readonly IpcInvoke<IpcNoRequest, IReadOnlyList<SaveBackupTargetDto>> GetTargets =
        new("saveBackup:targets:get", "saveBackupTargetsGet");
    public static readonly IpcInvoke<IpcNoRequest, SaveBackupDiscoverResultDto> DiscoverTargets =
        new("saveBackup:targets:discover", "saveBackupTargetsDiscover");
    public static readonly IpcInvoke<SaveBackupTargetAddRequest, SaveBackupTargetAddResultDto> AddTarget =
        new("saveBackup:target:add", "saveBackupTargetAdd");
    public static readonly IpcInvoke<IpcNoRequest, string?> PickDirectory =
        new("saveBackup:directory:pick", "saveBackupDirectoryPick", IsResponseNullable: true);
    public static readonly IpcInvoke<SaveBackupTargetUpdateRequest, bool> UpdateTarget =
        new("saveBackup:target:update", "saveBackupTargetUpdate");
    public static readonly IpcInvoke<SaveBackupTargetRemoveRequest, bool> RemoveTarget =
        new("saveBackup:target:remove", "saveBackupTargetRemove");
    public static readonly IpcInvoke<SaveBackupTargetIdRequest, bool> OpenTarget =
        new("saveBackup:target:open", "saveBackupTargetOpen");
    public static readonly IpcInvoke<SaveBackupRunRequest, bool> Run =
        new("saveBackup:run", "saveBackupRun", AllowsEmptyRequest: true);
    public static readonly IpcSend<IpcNoRequest> Cancel =
        new("saveBackup:cancel", "saveBackupCancel");
    public static readonly IpcInvoke<IpcNoRequest, SaveBackupStatusDto> GetStatus =
        new("saveBackup:status:get", "saveBackupStatusGet");
    public static readonly IpcHostEvent<SaveBackupStatusDto> Progress =
        new("saveBackup:progress", "saveBackupProgressOnListener", "saveBackupProgressRemoveListener");
    public static readonly IpcInvoke<SaveBackupTargetIdRequest, IReadOnlyList<SaveBackupSnapshotDto>> GetSnapshots =
        new("saveBackup:snapshots:get", "saveBackupSnapshotsGet");
    public static readonly IpcInvoke<SaveBackupSnapshotIdRequest, SaveBackupSnapshotFilesDto> GetSnapshotFiles =
        new("saveBackup:snapshot:files", "saveBackupSnapshotFilesGet");
    public static readonly IpcInvoke<SaveBackupSnapshotIdRequest, bool> DeleteSnapshot =
        new("saveBackup:snapshot:delete", "saveBackupSnapshotDelete");
    public static readonly IpcInvoke<SaveBackupSnapshotIdRequest, SaveBackupExportResultDto> ExportSnapshot =
        new("saveBackup:snapshot:export", "saveBackupSnapshotExport");
}

public static class SaveAnalysisIpc
{
    public static readonly IpcInvoke<IpcNoRequest, SaveAnalysisPluginListDto> GetPlugins =
        new("saveAnalysis:plugins:get", "saveAnalysisPluginsGet");
    public static readonly IpcInvoke<SaveAnalysisPluginEnabledRequest, bool> SetPluginEnabled =
        new("saveAnalysis:plugin:enabled", "saveAnalysisPluginSetEnabled");
    public static readonly IpcInvoke<SaveAnalysisPluginIdRequest, bool> UninstallPlugin =
        new("saveAnalysis:plugin:uninstall", "saveAnalysisPluginUninstall");
    public static readonly IpcInvoke<IpcNoRequest, bool> OpenPluginsFolder =
        new("saveAnalysis:plugins:openFolder", "saveAnalysisPluginsOpenFolder");
    public static readonly IpcInvoke<SaveAnalysisPluginFileRequest, SaveAnalysisPluginFileDto> GetPluginFile =
        new("saveAnalysis:plugin:file", "saveAnalysisPluginFileGet");
    public static readonly IpcInvoke<IpcNoRequest, SaveAnalysisRepositoryDto> GetRepository =
        new("saveAnalysis:repository:get", "saveAnalysisRepositoryGet");
    public static readonly IpcInvoke<SaveAnalysisPluginIdRequest, SaveAnalysisInstallResultDto> InstallPlugin =
        new("saveAnalysis:plugin:install", "saveAnalysisPluginInstall");
    public static readonly IpcInvoke<SaveAnalysisPluginIdRequest, IReadOnlyList<SaveAnalysisFileDto>> LocateSaves =
        new("saveAnalysis:saves:locate", "saveAnalysisSavesLocate");
    public static readonly IpcInvoke<IpcNoRequest, SaveAnalysisPickResultDto?> PickSave =
        new("saveAnalysis:save:pick", "saveAnalysisSavePick", IsResponseNullable: true);
    public static readonly IpcInvoke<SaveAnalysisReadRequest, SaveAnalysisChunkDto> ReadSave =
        new("saveAnalysis:save:read", "saveAnalysisSaveRead");
}

public static class ProfileHistoryIpc
{
    public static readonly IpcInvoke<ProfileHistoryQueryRequest, IReadOnlyList<ProfileHistoryEntryDto>> Get =
        new("profileHistory:get", "profileHistoryGet", AllowsEmptyRequest: true);
    public static readonly IpcInvoke<IpcNoRequest, ProfileHistoryCaptureResultDto> Capture =
        new("profileHistory:capture", "profileHistoryCapture");
}

public static class SessionIpc
{
    public static readonly IpcInvoke<SessionListQueryRequest, SessionListDto> GetList =
        new("session:list:get", "sessionListGet");
    public static readonly IpcInvoke<SessionUpdateRequest, SessionMutationResultDto> Update =
        new("session:update", "sessionUpdate");
    public static readonly IpcInvoke<SessionIdsRequest, SessionMutationResultDto> Merge =
        new("session:merge", "sessionMerge");
    public static readonly IpcInvoke<SessionIdsRequest, int> Delete =
        new("session:delete", "sessionDelete");
    public static readonly IpcInvoke<IpcNoRequest, IReadOnlyList<string>> GetTags =
        new("session:tags:get", "sessionTagsGet");
    public static readonly IpcInvoke<IpcNoRequest, IReadOnlyList<SessionAppDto>> GetApps =
        new("session:apps:get", "sessionAppsGet");
}

public static class HistoryIpc
{
    public static readonly IpcInvoke<PlaytimeHistoryQueryRequest, PlaytimeHistoryOverviewDto> GetOverview =
        new("history:overview:get", "historyOverviewGet", AllowsEmptyRequest: true);
    public static readonly IpcInvoke<IpcNoRequest, PlaytimeCaptureResultDto> Capture =
        new("history:snapshot:capture", "historySnapshotCapture");
    public static readonly IpcInvoke<SessionImportRequest, SessionImportPreviewDto> PreviewImport =
        new("history:import:preview", "historyImportPreview");
    public static readonly IpcInvoke<SessionImportRequest, SessionImportCommitResultDto> CommitImport =
        new("history:import:commit", "historyImportCommit");
    public static readonly IpcInvoke<IpcNoRequest, IReadOnlyList<SessionImportBatchDto>> GetImportBatches =
        new("history:import:batches:get", "historyImportBatchesGet");
    public static readonly IpcInvoke<SessionImportBatchDeleteRequest, int> DeleteImportBatch =
        new("history:import:batch:delete", "historyImportBatchDelete");
}

public static class StatsIpc
{
    public static readonly IpcInvoke<StatsDatasetRequest, StatsDatasetDto> GetDataset =
        new("stats:dataset:get", "statsDatasetGet");
}

public static class SemanticIpc
{
    public static readonly IpcInvoke<IpcNoRequest, SemanticCatalogDto> GetCatalog =
        new("semantic:catalog:get", "semanticCatalogGet");
    public static readonly IpcInvoke<SemanticQueryRequest, SemanticQueryResultDto> Query =
        new("semantic:query", "semanticQuery");
}

public static class InsightIpc
{
    public static readonly IpcInvoke<InsightReportRequest, InsightReportDto> GetReport =
        new("insight:report:get", "insightReportGet");
}

public static class WeeklyIpc
{
    public static readonly IpcInvoke<WeeklyIssuesQueryRequest, IReadOnlyList<WeeklyIssueSummaryDto>> GetIssues =
        new("weekly:issues:get", "weeklyIssuesGet");
    public static readonly IpcInvoke<WeeklyIssueIdRequest, WeeklyIssueDto?> GetIssue =
        new("weekly:issue:get", "weeklyIssueGet");
    public static readonly IpcInvoke<WeeklyIssueIdRequest, bool> MarkRead =
        new("weekly:issue:read", "weeklyIssueMarkRead");
    public static readonly IpcInvoke<WeeklyBackfillRequest, WeeklyBackfillResultDto> Backfill =
        new("weekly:issue:backfill", "weeklyIssueBackfill");
    public static readonly IpcHostEvent<WeeklyIssueSummaryDto> Published =
        new("weekly:published", "weeklyPublishedOnListener", "weeklyPublishedRemoveListener");
}

public static class MemoirIpc
{
    public static readonly IpcInvoke<TimeMachineRequest, TimeMachineDto> GetTimeMachine =
        new("memoir:timeMachine:get", "memoirTimeMachineGet");
    public static readonly IpcInvoke<ArchiveScopeRequest, MemoirChaptersDto> GetChapters =
        new("memoir:chapters:get", "memoirChaptersGet", AllowsEmptyRequest: true);
    public static readonly IpcInvoke<ArchiveScopeRequest, MemoirPrehistoryDto> GetPrehistory =
        new("memoir:prehistory:get", "memoirPrehistoryGet", AllowsEmptyRequest: true);
    public static readonly IpcInvoke<IpcNoRequest, IReadOnlyList<LifeEventDto>> GetLifeEvents =
        new("memoir:lifeEvents:get", "lifeEventsGet");
    public static readonly IpcInvoke<LifeEventSaveRequest, LifeEventDto> SaveLifeEvent =
        new("memoir:lifeEvent:save", "lifeEventSave");
    public static readonly IpcInvoke<LifeEventIdRequest, bool> DeleteLifeEvent =
        new("memoir:lifeEvent:delete", "lifeEventDelete");
    public static readonly IpcInvoke<IpcNoRequest, IReadOnlyList<TimeCapsuleDto>> GetCapsules =
        new("memoir:capsules:get", "timeCapsulesGet");
    public static readonly IpcInvoke<TimeCapsuleCreateRequest, TimeCapsuleDto> CreateCapsule =
        new("memoir:capsule:create", "timeCapsuleCreate");
    public static readonly IpcInvoke<TimeCapsuleIdRequest, TimeCapsuleDto?> OpenCapsule =
        new("memoir:capsule:open", "timeCapsuleOpen", IsResponseNullable: true);
    public static readonly IpcInvoke<TimeCapsuleIdRequest, bool> DeleteCapsule =
        new("memoir:capsule:delete", "timeCapsuleDelete");
    public static readonly IpcHostEvent<TimeCapsuleDto> CapsuleReady =
        new("memoir:capsule:ready", "timeCapsuleReadyOnListener", "timeCapsuleReadyRemoveListener");
    public static readonly IpcInvoke<PreviouslyOnRequest, PreviouslyOnDto?> GetPreviouslyOn =
        new("memoir:previouslyOn:get", "previouslyOnGet", IsResponseNullable: true);
    public static readonly IpcHostEvent<PreviouslyOnDto> PreviouslyOn =
        new("memoir:previouslyOn", "previouslyOnOnListener", "previouslyOnRemoveListener");
    public static readonly IpcInvoke<StaticSiteOptionsDto, StaticSitePreviewDto> PreviewSite =
        new("memoir:site:preview", "staticSitePreview");
    public static readonly IpcInvoke<StaticSiteExportRequest, StaticSiteExportResultDto> ExportSite =
        new("memoir:site:export", "staticSiteExport");
    public static readonly IpcInvoke<StaticSiteRevealRequest, bool> RevealSite =
        new("memoir:site:reveal", "staticSiteReveal");
}

public static class ArchiveCollectionIpc
{
    public static readonly IpcInvoke<IpcNoRequest, IReadOnlyList<ArchiveCollectorStatusDto>> GetCollectors =
        new("archive:collectors:get", "archiveCollectorsGet");
    public static readonly IpcInvoke<ArchiveCollectorRunRequest, ArchiveCollectorStatusDto?> RunCollector =
        new("archive:collector:run", "archiveCollectorRun", IsResponseNullable: true);
    public static readonly IpcInvoke<IpcNoRequest, IReadOnlyList<WebCaptureSummaryDto>> GetWebCaptures =
        new("archive:webCaptures:get", "archiveWebCapturesGet");
    public static readonly IpcInvoke<IpcNoRequest, int> ReparseWebCaptures =
        new("archive:webCaptures:reparse", "archiveWebCapturesReparse");
}

public static class GameArchiveIpc
{
    public static readonly IpcInvoke<GameArchiveRequest, GameArchiveDto> GetGame =
        new("archive:game:get", "gameArchiveGet");
    public static readonly IpcInvoke<PlayerCurveRequest, PlayerCurveDto> GetPlayerCurve =
        new("archive:game:players", "gamePlayerCurveGet");
    public static readonly IpcInvoke<RichPresenceMiningRequest, RichPresenceMiningDto> GetRichPresence =
        new("archive:game:richPresence", "gameRichPresenceGet");
    public static readonly IpcInvoke<ArchiveScopeRequest, PlatformOverviewDto> GetPlatforms =
        new("archive:platforms:get", "archivePlatformsGet");
    public static readonly IpcInvoke<ArchiveScopeRequest, PioneerReportDto> GetPioneer =
        new("archive:pioneer:get", "archivePioneerGet");
}

public static class SpendingIpc
{
    public static readonly IpcInvoke<PurchasePageRequest, PurchasePageDto> GetPurchases =
        new("archive:purchases:get", "archivePurchasesGet");
    public static readonly IpcInvoke<ArchiveScopeRequest, WishlistArchiveDto> GetWishlist =
        new("archive:wishlist:get", "archiveWishlistGet");
    public static readonly IpcInvoke<SimulatorSearchRequest, IReadOnlyList<SimulatorHitDto>> SearchSimulator =
        new("archive:simulator:search", "simulatorSearch");
    public static readonly IpcInvoke<SimulatorRunRequest, SimulatorResultDto> RunSimulator =
        new("archive:simulator:run", "simulatorRun");
    public static readonly IpcInvoke<ArchiveScopeRequest, StoreRegionDto> GetStoreRegion =
        new("archive:storeRegion:get", "storeRegionGet");
}

public static class ArtifactIpc
{
    public static readonly IpcInvoke<IpcNoRequest, ArtifactOverviewDto> GetOverview =
        new("archive:artifacts:get", "archiveArtifactsGet");
}

public static class CircleIpc
{
    public static readonly IpcInvoke<CircleFriendRequest, CircleTogetherDto> GetTogether =
        new("circle:together:get", "circleTogetherGet");
    public static readonly IpcInvoke<ArchiveScopeRequest, FriendshipDto> GetFriendship =
        new("circle:friendship:get", "circleFriendshipGet", AllowsEmptyRequest: true);
    public static readonly IpcInvoke<ArchiveScopeRequest, CompatibilityReportDto> GetCompatibility =
        new("circle:compatibility:get", "circleCompatibilityGet", AllowsEmptyRequest: true);
    public static readonly IpcInvoke<PartyPlanRequest, PartyPlanDto> PlanParty =
        new("circle:party:plan", "circlePartyPlan");
    public static readonly IpcInvoke<LeaderboardRequest, LeaderboardDto> GetLeaderboard =
        new("circle:leaderboard:get", "circleLeaderboardGet", AllowsEmptyRequest: true);
    public static readonly IpcInvoke<AdoptionRequest, AdoptionDto> GetAdoption =
        new("circle:adoption:get", "circleAdoptionGet", AllowsEmptyRequest: true);
    public static readonly IpcInvoke<CircleFilterRequest, FriendLibraryDto> GetLibraryEvents =
        new("circle:library:get", "circleLibraryEventsGet", AllowsEmptyRequest: true);
    public static readonly IpcInvoke<CircleFriendRequest, AchievementRaceDto> GetAchievementRace =
        new("circle:race:get", "circleAchievementRaceGet");
    public static readonly IpcInvoke<ExchangeCardExportRequest, ExchangeCardExportDto> ExportCard =
        new("circle:card:export", "exchangeCardExport");
    public static readonly IpcInvoke<ExchangeCardSaveRequest, bool> SaveCardFile =
        new("circle:card:saveFile", "exchangeCardSaveFile");
    public static readonly IpcInvoke<IpcNoRequest, ExchangeCardFileDto> OpenCardFile =
        new("circle:card:openFile", "exchangeCardOpenFile");
    public static readonly IpcInvoke<ExchangeCardImportRequest, ExchangeCardImportResultDto> ImportCard =
        new("circle:card:import", "exchangeCardImport");
    public static readonly IpcInvoke<IpcNoRequest, IReadOnlyList<ExchangeCardDto>> GetCards =
        new("circle:cards:get", "exchangeCardsGet");
    public static readonly IpcInvoke<ExchangeCardIdRequest, bool> DeleteCard =
        new("circle:card:delete", "exchangeCardDelete");
    public static readonly IpcInvoke<DuoReviewRequest, DuoReviewDto?> GetDuoReview =
        new("circle:duo:get", "duoReviewGet", IsResponseNullable: true);
}

public static class CommunityArchiveIpc
{
    public static readonly IpcInvoke<ArchiveScopeRequest, CommunityArchiveDto> GetCommunity =
        new("archive:community:get", "archiveCommunityGet");
    public static readonly IpcInvoke<CommunityCommentsRequest, CommunityCommentPageDto> GetComments =
        new("archive:community:comments", "archiveCommentsGet");
    public static readonly IpcInvoke<IpcNoRequest, FamilyArchiveDto> GetFamily =
        new("archive:family:get", "archiveFamilyGet");
    public static readonly IpcInvoke<FriendPlaytimeRequest, FriendPlaytimeDto> GetFriendPlaytime =
        new("archive:friendPlaytime:get", "friendPlaytimeGet");
}

public static class RecordingIpc
{
    public static readonly IpcInvoke<ArchiveScopeRequest, RecordingArchiveDto> GetRecordings =
        new("archive:recordings:get", "archiveRecordingsGet");
    public static readonly IpcInvoke<RecordingActionRequest, bool> Reveal =
        new("archive:recording:reveal", "archiveRecordingReveal");
}

public static class SpectrumIpc
{
    public static readonly IpcInvoke<ScreenshotPaletteRequest, IReadOnlyList<int>> GetPending =
        new("archive:palette:pending", "screenshotPalettePending");
    public static readonly IpcInvoke<ScreenshotPaletteImageRequest, string?> GetImage =
        new("archive:palette:image", "screenshotPaletteImage", IsResponseNullable: true);
    public static readonly IpcInvoke<ScreenshotPaletteSaveRequest, int> Save =
        new("archive:palette:save", "screenshotPaletteSave");
    public static readonly IpcInvoke<IpcNoRequest, ScreenshotSpectrumDto> GetSpectrum =
        new("archive:spectrum:get", "screenshotSpectrumGet");
    public static readonly IpcInvoke<PosterSaveRequest, PosterSaveResultDto> SavePoster =
        new("archive:poster:save", "posterSave");
}

public static class ReliabilityIpc
{
    public static readonly IpcInvoke<IpcNoRequest, ArchiveHealthReportDto> RunHealthCheck =
        new("archive:health:run", "archiveHealthRun");
    public static readonly IpcInvoke<StatsExplainRequest, StatsExplainDto> Explain =
        new("stats:explain", "statsExplain");
    public static readonly IpcInvoke<IpcNoRequest, IReadOnlyList<DatabaseSnapshotDto>> GetSnapshots =
        new("database:snapshots:get", "databaseSnapshotsGet");
    public static readonly IpcInvoke<DatabaseSnapshotRestoreRequest, BackupRestoreResultDto> RestoreSnapshot =
        new("database:snapshot:restore", "databaseSnapshotRestore");
    public static readonly IpcInvoke<IpcNoRequest, ArchiveSealOverviewDto> GetSeals =
        new("archive:seals:get", "archiveSealsGet");
    public static readonly IpcInvoke<ArchiveSealCreateRequest, ArchiveSealResultDto> CreateSeal =
        new("archive:seal:create", "archiveSealCreate");
    public static readonly IpcInvoke<ArchiveSealRevealRequest, bool> RevealSeal =
        new("archive:seal:reveal", "archiveSealReveal");
    public static readonly IpcInvoke<IpcNoRequest, IReadOnlyList<RichPresenceCoverageDto>> GetRichPresenceCoverage =
        new("friends:richPresence:coverage", "richPresenceCoverageGet");
}

public static class JobIpc
{
    public static readonly IpcInvoke<IpcNoRequest, UpdateAppRunningStatusJobStatusDto> GetUpdateAppRunningStatus =
        new("job:updateAppRunningStatus:get", "jobGetUpdateAppRunningStatusJobStatus");
}

public static class SettingIpc
{
    public static readonly IpcInvoke<IpcNoRequest, AppSettingsDto> Get =
        new("setting:get", "settingGet");
    public static readonly IpcInvoke<AppSettingsPatchDto, bool> Update =
        new("setting:update", "settingUpdate");
    /// <summary>设置已更新。只发给独立窗口：它们的外观跟随主窗口。</summary>
    public static readonly IpcHostEvent<AppSettingsDto> Changed =
        new("setting:changed", "settingChangedOnListener", "settingChangedRemoveListener");
}

public static class NetworkIpc
{
    public static readonly IpcInvoke<IpcNoRequest, NetworkDiagnosticsDto> RunDiagnostics =
        new("network:diagnostics:run", "networkDiagnosticsRun");
}

public static class UpdaterIpc
{
    public static readonly IpcInvoke<IpcNoRequest, UpdaterStatusDto> GetStatus =
        new("updater:status:get", "updaterGetStatus");
    public static readonly IpcSend<IpcNoRequest> Check = new("updater:check", "updaterCheck");
    public static readonly IpcSend<IpcNoRequest> Download = new("updater:download", "updaterDownload");
    public static readonly IpcSend<IpcNoRequest> QuitAndInstall =
        new("updater:quitAndInstall", "updaterQuitAndInstall");
    public static readonly IpcHostEvent<UpdaterEventDto> Event =
        new("updater:event", "updaterEventOnListener", "updaterEventRemoveListener");
}

public static class DataExportIpc
{
    public static readonly IpcInvoke<DataExportRequest, DataExportResultDto> Run =
        new("dataExport:run", "dataExportRun");
    public static readonly IpcInvoke<DataExportRevealRequest, bool> Reveal =
        new("dataExport:reveal", "dataExportReveal");
}

public static class BackupIpc
{
    public static readonly IpcInvoke<BackupCreateRequest, BackupCreateResultDto> Create =
        new("backup:create", "backupCreate");
    public static readonly IpcSend<IpcNoRequest> CancelCreate =
        new("backup:create:cancel", "backupCreateCancel");
    public static readonly IpcInvoke<IpcNoRequest, BackupInspectResultDto> PickRestore =
        new("backup:restore:pick", "backupRestorePick");
    public static readonly IpcInvoke<BackupUnlockRequest, BackupInspectResultDto> Unlock =
        new("backup:restore:unlock", "backupRestoreUnlock");
    public static readonly IpcInvoke<BackupRestoreRequest, BackupRestoreResultDto> StageRestore =
        new("backup:restore:stage", "backupRestoreStage");
    public static readonly IpcInvoke<IpcNoRequest, bool> CancelRestore =
        new("backup:restore:cancel", "backupRestoreCancel");
    public static readonly IpcInvoke<IpcNoRequest, BackupRestoreStatusDto> GetRestoreStatus =
        new("backup:restore:status", "backupRestoreStatusGet");
    public static readonly IpcInvoke<IpcNoRequest, BackupRendererStateDto> TakeRendererState =
        new("backup:rendererState:take", "backupRendererStateTake");
}

public static class SyncIpc
{
    public static readonly IpcInvoke<IpcNoRequest, SyncStatusDto> GetStatus = new("sync:status:get", "syncStatusGet");
    public static readonly IpcInvoke<SyncConnectRequest, SyncTestResultDto> Test = new("sync:test", "syncTest");
    public static readonly IpcInvoke<SyncSetupRequest, SyncSetupResultDto> Setup = new("sync:setup", "syncSetup");
    public static readonly IpcInvoke<IpcNoRequest, bool> Run = new("sync:run", "syncRun");
    public static readonly IpcSend<IpcNoRequest> Cancel = new("sync:cancel", "syncCancel");
    public static readonly IpcInvoke<SyncDisableRequest, bool> Disable = new("sync:disable", "syncDisable");
    public static readonly IpcInvoke<SyncPassphraseChangeRequest, bool> ChangePassphrase =
        new("sync:passphrase:change", "syncPassphraseChange");
    public static readonly IpcInvoke<IpcNoRequest, string?> PickFolder =
        new("sync:folder:pick", "syncFolderPick", IsResponseNullable: true);
    public static readonly IpcHostEvent<SyncStatusDto> Progress =
        new("sync:progress", "syncProgressOnListener", "syncProgressRemoveListener");
}

/// <summary>渲染层发起的长任务的进度（库同步、截图索引、备份等），由 kind 区分。</summary>
public static class TaskIpc
{
    public static readonly IpcHostEvent<TaskProgressDto> Progress =
        new("task:progress", "taskProgressOnListener", "taskProgressRemoveListener");
}

public static class PublicDataIpc
{
    public static readonly IpcInvoke<IpcNoRequest, PublicDataStatusDto> GetStatus = new("publicData:status:get", "publicDataStatusGet");
    public static readonly IpcInvoke<IpcNoRequest, PublicDataStatusDto> Refresh = new("publicData:refresh", "publicDataRefresh");
    public static readonly IpcInvoke<PublicDataEnabledRequest, PublicDataStatusDto> SetEnabled =
        new("publicData:enabled:set", "publicDataEnabledSet");
}

public static class AppInfoIpc
{
    public static readonly IpcInvoke<IpcNoRequest, AppInfoDto> Get = new("app:info:get", "appInfoGet");
}

public static class DiagnosticsIpc
{
    public static readonly IpcInvoke<DiagnosticsBundleRequest, DiagnosticsBundleDto> CreateBundle =
        new("diagnostics:bundle:create", "diagnosticsBundleCreate", AllowsEmptyRequest: true);
    public static readonly IpcSend<DiagnosticsBundleRevealRequest> RevealBundle =
        new("diagnostics:bundle:reveal", "diagnosticsBundleReveal");
    public static readonly IpcInvoke<FeedbackIssueRequest, bool> OpenIssue =
        new("feedback:issue:open", "feedbackIssueOpen");
}

public static class CrashReportIpc
{
    public static readonly IpcInvoke<IpcNoRequest, CrashReportListDto> List = new("crashReport:list", "crashReportList");
    public static readonly IpcInvoke<CrashReportIdRequest, CrashReportSubmitResultDto> Submit =
        new("crashReport:submit", "crashReportSubmit");
    public static readonly IpcInvoke<CrashReportDismissRequest, bool> Dismiss =
        new("crashReport:dismiss", "crashReportDismiss", AllowsEmptyRequest: true);
    public static readonly IpcSend<RendererCrashReportRequest> Record = new("crashReport:record", "crashReportRecord");
}

public static class AppWindowIpc
{
    public static readonly IpcSend<IpcNoRequest> Quit = new("app:quit", "appQuit");
    public static readonly IpcInvoke<IpcNoRequest, AppRelaunchResultDto> Relaunch = new("app:relaunch", "appRelaunch");
    public static readonly IpcSend<IpcNoRequest> MinimizeToTray =
        new("window:minimizeToTray", "windowMinimizeToTray");
    public static readonly IpcSend<IpcNoRequest> Minimize = new("window:minimize", "windowMinimize");
    public static readonly IpcInvoke<IpcNoRequest, bool> Maximize = new("window:maximize", "windowMaximize");
    public static readonly IpcSend<IpcNoRequest> Close = new("window:close", "windowClose");
    public static readonly IpcInvoke<IpcNoRequest, bool> IsMaximized =
        new("window:isMaximized", "windowIsMaximized");
}

public static class PopoutIpc
{
    public static readonly IpcInvoke<PopoutOpenRequest, PopoutOpenResultDto> Open = new("popout:open", "popoutOpen");
    public static readonly IpcInvoke<PopoutControlRequest, PopoutStateDto> Control = new("popout:control", "popoutControl");
}

public static class AppearanceIpc
{
    public static readonly IpcInvoke<IpcNoRequest, AppearanceBackgroundPickResultDto> PickBackground =
        new("appearance:background:pick", "appearanceBackgroundPick");
}

public static class TrayIpc
{
    public static readonly IpcSend<TrayStateRequest> UpdateState = new("tray:state:update", "trayStateUpdate");
    public static readonly IpcHostEvent<TrayCommandDto> Command =
        new("tray:command", "trayCommandOnListener", "trayCommandRemoveListener");
}

public static class NotificationIpc
{
    public static readonly IpcSend<NotificationShowRequest> Show = new("notification:show", "notificationShow");
}

public static class ShellIpc
{
    public static readonly IpcSend<string> OpenExternal = new("shell:openExternal", "shellOpenExternal");
    public static readonly IpcSend<string> OpenPath = new("shell:openPath", "shellOpenPath");
}

public static class IpcCatalog
{
    public static IReadOnlyList<IIpcEndpointDescriptor> All { get; } =
    [
        SteamIpc.GetStatus,
        SteamIpc.RefreshStatus,
        SteamIpc.GetLibraryFolders,
        SteamIpc.RunAppAction,
        SteamIpc.GetOperationalStatus,
        SteamIpc.GetLocalDataSources,
        SteamIpc.GetLoginUsers,
        SteamIpc.RefreshLoginUsers,
        SteamIpc.ChangeLoginUser,
        SteamIpc.LoginUsersUpdated,
        SteamIpc.GetRunningApps,
        SteamIpc.GetAppsInfo,
        SteamIpc.GetAppImages,
        SteamIpc.RefreshAppsInfo,
        SteamIpc.GetValidUseAppRecords,
        SteamIpc.GetCurrentSessions,
        SteamIpc.GetUsersInRecords,
        SteamIpc.EndUseAppRecording,
        SteamIpc.DiscardUseAppRecording,
        SessionIpc.GetList,
        SessionIpc.Update,
        SessionIpc.Merge,
        SessionIpc.Delete,
        SessionIpc.GetTags,
        SessionIpc.GetApps,
        HistoryIpc.GetOverview,
        HistoryIpc.Capture,
        HistoryIpc.PreviewImport,
        HistoryIpc.CommitImport,
        HistoryIpc.GetImportBatches,
        HistoryIpc.DeleteImportBatch,
        StatsIpc.GetDataset,
        SemanticIpc.GetCatalog,
        SemanticIpc.Query,
        InsightIpc.GetReport,
        WeeklyIpc.GetIssues,
        WeeklyIpc.GetIssue,
        WeeklyIpc.MarkRead,
        WeeklyIpc.Backfill,
        WeeklyIpc.Published,
        MemoirIpc.GetTimeMachine,
        MemoirIpc.GetChapters,
        MemoirIpc.GetPrehistory,
        MemoirIpc.GetLifeEvents,
        MemoirIpc.SaveLifeEvent,
        MemoirIpc.DeleteLifeEvent,
        MemoirIpc.GetCapsules,
        MemoirIpc.CreateCapsule,
        MemoirIpc.OpenCapsule,
        MemoirIpc.DeleteCapsule,
        MemoirIpc.CapsuleReady,
        MemoirIpc.GetPreviouslyOn,
        MemoirIpc.PreviouslyOn,
        MemoirIpc.PreviewSite,
        MemoirIpc.ExportSite,
        MemoirIpc.RevealSite,
        ArchiveCollectionIpc.GetCollectors,
        ArchiveCollectionIpc.RunCollector,
        ArchiveCollectionIpc.GetWebCaptures,
        ArchiveCollectionIpc.ReparseWebCaptures,
        GameArchiveIpc.GetGame,
        GameArchiveIpc.GetPlayerCurve,
        GameArchiveIpc.GetRichPresence,
        GameArchiveIpc.GetPlatforms,
        GameArchiveIpc.GetPioneer,
        SpendingIpc.GetPurchases,
        SpendingIpc.GetWishlist,
        SpendingIpc.SearchSimulator,
        SpendingIpc.RunSimulator,
        SpendingIpc.GetStoreRegion,
        ArtifactIpc.GetOverview,
        CommunityArchiveIpc.GetCommunity,
        CommunityArchiveIpc.GetComments,
        CommunityArchiveIpc.GetFamily,
        CommunityArchiveIpc.GetFriendPlaytime,
        CircleIpc.GetTogether,
        CircleIpc.GetFriendship,
        CircleIpc.GetCompatibility,
        CircleIpc.PlanParty,
        CircleIpc.GetLeaderboard,
        CircleIpc.GetAdoption,
        CircleIpc.GetLibraryEvents,
        CircleIpc.GetAchievementRace,
        CircleIpc.ExportCard,
        CircleIpc.SaveCardFile,
        CircleIpc.OpenCardFile,
        CircleIpc.ImportCard,
        CircleIpc.GetCards,
        CircleIpc.DeleteCard,
        CircleIpc.GetDuoReview,
        RecordingIpc.GetRecordings,
        RecordingIpc.Reveal,
        SpectrumIpc.GetPending,
        SpectrumIpc.GetImage,
        SpectrumIpc.Save,
        SpectrumIpc.GetSpectrum,
        SpectrumIpc.SavePoster,
        ReliabilityIpc.RunHealthCheck,
        ReliabilityIpc.Explain,
        ReliabilityIpc.GetSnapshots,
        ReliabilityIpc.RestoreSnapshot,
        ReliabilityIpc.GetSeals,
        ReliabilityIpc.CreateSeal,
        ReliabilityIpc.RevealSeal,
        ReliabilityIpc.GetRichPresenceCoverage,
        SteamLoginIpc.StartCredentials,
        SteamLoginIpc.StartQr,
        SteamLoginIpc.StartToken,
        SteamLoginIpc.SubmitGuardCode,
        SteamLoginIpc.SwitchToUseCode,
        SteamLoginIpc.ConfirmDevice,
        SteamLoginIpc.Cancel,
        SteamLoginIpc.GetLoggedInUsers,
        SteamLoginIpc.LogoutUser,
        SteamLoginIpc.GetSavedTokens,
        SteamLoginIpc.DeleteSavedToken,
        SteamLoginIpc.UpdateSavedTokenOptions,
        SteamLoginIpc.SetPersonaState,
        SteamLoginIpc.Event,
        SteamFriendsIpc.GetSnapshot,
        SteamFriendsIpc.Refresh,
        SteamFriendsIpc.Updated,
        SteamFriendsIpc.StartTracking,
        SteamFriendsIpc.StopTracking,
        SteamFriendsIpc.GetTracking,
        SteamFriendsIpc.GetRecords,
        SteamFriendsIpc.ClearRecords,
        SteamFriendsIpc.GetLibrary,
        SteamFriendsIpc.GetAchievements,
        SteamLibraryIpc.GetSnapshot,
        SteamLibraryIpc.Refresh,
        SteamLibraryIpc.CancelRefresh,
        SteamLibraryIpc.GetPage,
        AchievementIpc.GetOverview,
        AchievementIpc.GetGame,
        AchievementIpc.RefreshGame,
        AchievementArchiveIpc.GetUnlocks,
        AchievementArchiveIpc.GetRecentUnlocks,
        AchievementArchiveIpc.GetGames,
        AchievementArchiveIpc.GetSets,
        AchievementArchiveIpc.GetLedger,
        AchievementArchiveIpc.GetChanges,
        AchievementArchiveIpc.StartSync,
        AchievementArchiveIpc.CancelSync,
        AchievementArchiveIpc.GetSyncStatus,
        AchievementArchiveIpc.SyncProgress,
        AchievementArchiveIpc.GetTargets,
        AchievementArchiveIpc.AddTarget,
        AchievementArchiveIpc.RemoveTarget,
        LibraryArchiveIpc.RefreshAcquisitions,
        LibraryArchiveIpc.GetAcquisitions,
        LibraryArchiveIpc.GetGameMeta,
        LibraryArchiveIpc.UpdateGameMeta,
        ScreenshotIpc.RefreshIndex,
        ScreenshotIpc.GetList,
        ScreenshotIpc.GetApps,
        ScreenshotIpc.RunAction,
        SaveBackupIpc.GetTargets,
        SaveBackupIpc.DiscoverTargets,
        SaveBackupIpc.AddTarget,
        SaveBackupIpc.PickDirectory,
        SaveBackupIpc.UpdateTarget,
        SaveBackupIpc.RemoveTarget,
        SaveBackupIpc.OpenTarget,
        SaveBackupIpc.Run,
        SaveBackupIpc.Cancel,
        SaveBackupIpc.GetStatus,
        SaveBackupIpc.Progress,
        SaveBackupIpc.GetSnapshots,
        SaveBackupIpc.GetSnapshotFiles,
        SaveBackupIpc.DeleteSnapshot,
        SaveBackupIpc.ExportSnapshot,
        SaveAnalysisIpc.GetPlugins,
        SaveAnalysisIpc.SetPluginEnabled,
        SaveAnalysisIpc.UninstallPlugin,
        SaveAnalysisIpc.OpenPluginsFolder,
        SaveAnalysisIpc.GetPluginFile,
        SaveAnalysisIpc.GetRepository,
        SaveAnalysisIpc.InstallPlugin,
        SaveAnalysisIpc.LocateSaves,
        SaveAnalysisIpc.PickSave,
        SaveAnalysisIpc.ReadSave,
        ProfileHistoryIpc.Get,
        ProfileHistoryIpc.Capture,
        JobIpc.GetUpdateAppRunningStatus,
        SettingIpc.Get,
        SettingIpc.Update,
        NetworkIpc.RunDiagnostics,
        UpdaterIpc.GetStatus,
        UpdaterIpc.Check,
        UpdaterIpc.Download,
        UpdaterIpc.QuitAndInstall,
        UpdaterIpc.Event,
        DataExportIpc.Run,
        DataExportIpc.Reveal,
        BackupIpc.Create,
        BackupIpc.CancelCreate,
        BackupIpc.PickRestore,
        BackupIpc.Unlock,
        BackupIpc.StageRestore,
        BackupIpc.CancelRestore,
        BackupIpc.GetRestoreStatus,
        BackupIpc.TakeRendererState,
        SyncIpc.GetStatus,
        SyncIpc.Test,
        SyncIpc.Setup,
        SyncIpc.Run,
        SyncIpc.Cancel,
        SyncIpc.Disable,
        SyncIpc.ChangePassphrase,
        SyncIpc.PickFolder,
        SyncIpc.Progress,
        TaskIpc.Progress,
        PublicDataIpc.GetStatus,
        PublicDataIpc.Refresh,
        PublicDataIpc.SetEnabled,
        AppInfoIpc.Get,
        DiagnosticsIpc.CreateBundle,
        DiagnosticsIpc.RevealBundle,
        DiagnosticsIpc.OpenIssue,
        CrashReportIpc.List,
        CrashReportIpc.Submit,
        CrashReportIpc.Dismiss,
        CrashReportIpc.Record,
        AppWindowIpc.Quit,
        AppWindowIpc.Relaunch,
        AppWindowIpc.MinimizeToTray,
        AppWindowIpc.Minimize,
        AppWindowIpc.Maximize,
        AppWindowIpc.Close,
        AppWindowIpc.IsMaximized,
        SettingIpc.Changed,
        PopoutIpc.Open,
        PopoutIpc.Control,
        AppearanceIpc.PickBackground,
        TrayIpc.UpdateState,
        TrayIpc.Command,
        NotificationIpc.Show,
        ShellIpc.OpenExternal,
        ShellIpc.OpenPath
    ];
}
