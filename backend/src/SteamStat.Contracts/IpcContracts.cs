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
    public static readonly IpcInvoke<SteamAppsQueryRequest, IReadOnlyList<SteamAppDto>> RefreshAppsInfo =
        new("steam:appsInfo:refresh", "steamRefreshAppsInfo", AllowsEmptyRequest: true);
    public static readonly IpcInvoke<UseAppRecordsQueryRequest, UseAppRecordsDto> GetValidUseAppRecords =
        new("steam:validUseAppRecord:get", "steamGetValidUseAppRecord", AllowsEmptyRequest: true);
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
    public static readonly IpcInvoke<SteamPersonaStateRequest, bool> SetPersonaState =
        new("steamLogin:user:setPersonaState", "steamLoginUserSetPersonaState");
    public static readonly IpcHostEvent<SteamLoginEventDto> Event =
        new("steamLogin:event", "steamLoginEventOnListener", "steamLoginEventRemoveListener");
}

public static class SteamFriendsIpc
{
    public static readonly IpcInvoke<IpcNoRequest, IReadOnlyList<SteamFriendsDataDto>> GetAll =
        new("steamFriends:getAll", "steamFriendsGetAll");
    public static readonly IpcInvoke<AccountNameRequest, SteamFriendsDataDto?> GetForUser =
        new("steamFriends:getForUser", "steamFriendsGetForUser", IsResponseNullable: true);
    public static readonly IpcInvoke<IpcNoRequest, IReadOnlyList<SteamFriendsDataDto>> GetCached =
        new("steamFriends:getCached", "steamFriendsGetCached");
    public static readonly IpcSend<SteamFriendInfoRequest> RequestFriendInfo =
        new("steamFriends:requestFriendInfo", "steamFriendsRequestFriendInfo");
    public static readonly IpcHostEvent<SteamFriendsUpdatedEventDto> Updated =
        new("steamFriends:update", "steamFriendsUpdateOnListener", "steamFriendsUpdateRemoveListener");
    public static readonly IpcInvoke<SteamFriendsTrackingRequest, bool> StartTracking =
        new("steamFriends:track:start", "steamFriendsTrackStart");
    public static readonly IpcInvoke<SteamFriendsTrackingRequest, bool> StopTracking =
        new("steamFriends:track:stop", "steamFriendsTrackStop");
    public static readonly IpcInvoke<AccountNameRequest, IReadOnlyList<string>> GetTracking =
        new("steamFriends:track:get", "steamFriendsTrackGet");
    public static readonly IpcInvoke<IpcNoRequest, IReadOnlyDictionary<string, IReadOnlyList<string>>> GetAllTracking =
        new("steamFriends:track:getAll", "steamFriendsTrackGetAll");
    public static readonly IpcInvoke<FriendStatusRecordsQueryRequest, IReadOnlyList<FriendStatusRecordDto>> GetRecords =
        new("steamFriends:records:get", "steamFriendsRecordsGet", AllowsEmptyRequest: true);
    public static readonly IpcInvoke<FriendStatusRecordsClearRequest, int> ClearRecords =
        new("steamFriends:records:clear", "steamFriendsRecordsClear", AllowsEmptyRequest: true);
}

public static class SteamLibraryIpc
{
    public static readonly IpcInvoke<AccountNameRequest, IReadOnlyList<SteamOwnedGameDto>> GetForUser =
        new("steamLibrary:getForUser", "steamLibraryGetForUser");
    public static readonly IpcInvoke<IpcNoRequest, IReadOnlyDictionary<string, IReadOnlyList<SteamOwnedGameDto>>> GetForAllUsers =
        new("steamLibrary:getForAllUsers", "steamLibraryGetForAllUsers");
    public static readonly IpcInvoke<AccountNameRequest, bool> SyncForUser =
        new("steamLibrary:syncForUser", "steamLibrarySyncForUser");
    public static readonly IpcInvoke<IpcNoRequest, IReadOnlyDictionary<string, bool>> SyncForAllUsers =
        new("steamLibrary:syncForAllUsers", "steamLibrarySyncForAllUsers");
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

public static class AppWindowIpc
{
    public static readonly IpcSend<IpcNoRequest> Quit = new("app:quit", "appQuit");
    public static readonly IpcSend<IpcNoRequest> MinimizeToTray =
        new("window:minimizeToTray", "windowMinimizeToTray");
    public static readonly IpcSend<IpcNoRequest> Minimize = new("window:minimize", "windowMinimize");
    public static readonly IpcInvoke<IpcNoRequest, bool> Maximize = new("window:maximize", "windowMaximize");
    public static readonly IpcSend<IpcNoRequest> Close = new("window:close", "windowClose");
    public static readonly IpcInvoke<IpcNoRequest, bool> IsMaximized =
        new("window:isMaximized", "windowIsMaximized");
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
        SteamIpc.GetLoginUsers,
        SteamIpc.RefreshLoginUsers,
        SteamIpc.ChangeLoginUser,
        SteamIpc.LoginUsersUpdated,
        SteamIpc.GetRunningApps,
        SteamIpc.GetAppsInfo,
        SteamIpc.RefreshAppsInfo,
        SteamIpc.GetValidUseAppRecords,
        SteamIpc.GetUsersInRecords,
        SteamIpc.EndUseAppRecording,
        SteamIpc.DiscardUseAppRecording,
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
        SteamLoginIpc.SetPersonaState,
        SteamLoginIpc.Event,
        SteamFriendsIpc.GetAll,
        SteamFriendsIpc.GetForUser,
        SteamFriendsIpc.GetCached,
        SteamFriendsIpc.RequestFriendInfo,
        SteamFriendsIpc.Updated,
        SteamFriendsIpc.StartTracking,
        SteamFriendsIpc.StopTracking,
        SteamFriendsIpc.GetTracking,
        SteamFriendsIpc.GetAllTracking,
        SteamFriendsIpc.GetRecords,
        SteamFriendsIpc.ClearRecords,
        SteamLibraryIpc.GetForUser,
        SteamLibraryIpc.GetForAllUsers,
        SteamLibraryIpc.SyncForUser,
        SteamLibraryIpc.SyncForAllUsers,
        JobIpc.GetUpdateAppRunningStatus,
        SettingIpc.Get,
        SettingIpc.Update,
        UpdaterIpc.GetStatus,
        UpdaterIpc.Check,
        UpdaterIpc.Download,
        UpdaterIpc.QuitAndInstall,
        UpdaterIpc.Event,
        AppWindowIpc.Quit,
        AppWindowIpc.MinimizeToTray,
        AppWindowIpc.Minimize,
        AppWindowIpc.Maximize,
        AppWindowIpc.Close,
        AppWindowIpc.IsMaximized,
        ShellIpc.OpenExternal,
        ShellIpc.OpenPath
    ];
}
