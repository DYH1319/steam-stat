using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamStat.Contracts.Ipc;

public sealed record GlobalStatusDto
{
    public required int Id { get; init; }
    [IpcOptional] public string? SteamPath { get; init; }
    [IpcOptional] public string? SteamExePath { get; init; }
    [IpcOptional] public int? SteamPid { get; init; }
    [IpcOptional] public string? SteamClientDllPath { get; init; }
    [IpcOptional] public string? SteamClientDll64Path { get; init; }
    [IpcOptional] public string? ActiveUserSteamId { get; init; }
    [IpcOptional] public int? RunningAppId { get; init; }
    public required int RefreshTime { get; init; }
    [IpcOptional] public int? SteamUserRefreshTime { get; init; }
    [IpcOptional] public int? SteamAppRefreshTime { get; init; }
}

public sealed record SteamUserDto
{
    public required int Id { get; init; }
    public required string SteamId { get; init; }
    public required int AccountId { get; init; }
    public required string AccountName { get; init; }
    [IpcOptional] public string? PersonaName { get; init; }
    [IpcOptional] public bool? RememberPassword { get; init; }
    [IpcOptional] public bool? WantsOfflineMode { get; init; }
    [IpcOptional] public bool? SkipOfflineModeWarning { get; init; }
    [IpcOptional] public bool? AllowAutoLogin { get; init; }
    [IpcOptional] public bool? MostRecent { get; init; }
    [IpcOptional] public int? Timestamp { get; init; }
    [IpcOptional] public string? AvatarFull { get; init; }
    [IpcOptional] public string? AvatarMedium { get; init; }
    [IpcOptional] public string? AvatarSmall { get; init; }
    [IpcOptional] public string? AnimatedAvatar { get; init; }
    [IpcOptional] public string? AvatarFrame { get; init; }
    [IpcOptional] public int? Level { get; init; }
    [IpcOptional] public string? LevelClass { get; init; }
}

public sealed record ChangeSteamUserRequest
{
    public required int Id { get; init; }
    public required string SteamId { get; init; }
    public required int AccountId { get; init; }
    [IpcMaxLength(64)] public required string AccountName { get; init; }
    [IpcOptional] public string? PersonaName { get; init; }
    [IpcOptional] public bool? RememberPassword { get; init; }
    [IpcOptional] public bool? WantsOfflineMode { get; init; }
    [IpcOptional] public bool? SkipOfflineModeWarning { get; init; }
    [IpcOptional] public bool? AllowAutoLogin { get; init; }
    [IpcOptional] public bool? MostRecent { get; init; }
    [IpcOptional] public int? Timestamp { get; init; }
    [IpcOptional] public string? AvatarFull { get; init; }
    [IpcOptional] public string? AvatarMedium { get; init; }
    [IpcOptional] public string? AvatarSmall { get; init; }
    [IpcOptional] public string? AnimatedAvatar { get; init; }
    [IpcOptional] public string? AvatarFrame { get; init; }
    [IpcOptional] public int? Level { get; init; }
    [IpcOptional] public string? LevelClass { get; init; }
    [IpcOptional] public bool? OfflineMode { get; init; }
    [IpcOptional, IpcRange(0, 7)] public int? PersonaState { get; init; }
}

public sealed record SteamAppsQueryRequest
{
    [IpcOptional, IpcStringValues("appId", "name", "installDir", "appOnDisk")]
    public string? SortField { get; init; }
    [IpcOptional, IpcStringValues("asc", "desc")] public string? SortOrder { get; init; }
    [IpcOptional] public bool? FilterInstalled { get; init; }
}

public sealed record SteamAppDto
{
    public required int Id { get; init; }
    public required int AppId { get; init; }
    [IpcOptional] public string? Name { get; init; }
    public required string NameLocalized { get; init; }
    public required bool Installed { get; init; }
    [IpcOptional] public string? InstallDir { get; init; }
    [IpcOptional] public string? InstallDirPath { get; init; }
    [IpcOptional, IpcNumber] public long? AppOnDisk { get; init; }
    [IpcOptional, IpcNumber] public long? AppOnDiskReal { get; init; }
    public required bool IsRunning { get; init; }
    [IpcOptional] public string? Type { get; init; }
    [IpcOptional] public string? Developer { get; init; }
    [IpcOptional] public string? Publisher { get; init; }
    [IpcOptional] public int? SteamReleaseDate { get; init; }
    [IpcOptional] public bool? IsFreeApp { get; init; }
}

public sealed record RunningAppsDto(IReadOnlyList<SteamAppDto> Apps, [property: IpcNumber] long LastUpdateTime);

public sealed record UseAppRecordsQueryRequest
{
    [IpcOptional] public IReadOnlyList<string>? SteamIds { get; init; }
    [IpcOptional] public int? StartDate { get; init; }
    [IpcOptional] public int? EndDate { get; init; }
}

public sealed record UseAppRecordDto
{
    public required int AppId { get; init; }
    public required string SteamId { get; init; }
    public required int StartTime { get; init; }
    public required int EndTime { get; init; }
    public required int Duration { get; init; }
    [IpcOptional] public string? AppName { get; init; }
    [IpcOptional] public string? AppNameLocalized { get; init; }
    [IpcOptional] public string? UserPersonaName { get; init; }
}

public sealed record UseAppRecordsDto(
    IReadOnlyList<UseAppRecordDto> Records,
    [property: IpcNumber] long LastUpdateTime);

public sealed record SteamLoginCredentialsRequest
{
    [IpcMaxLength(64)] public required string Username { get; init; }
    [IpcMaxLength(1024)] public required string Password { get; init; }
    public required bool RememberMe { get; init; }
}

public sealed record SteamLoginQrRequest
{
    public required bool RememberMe { get; init; }
}

public sealed record SteamLoginTokenRequest
{
    [IpcRange(1, int.MaxValue)] public required int TokenId { get; init; }
}

public sealed record SteamLoginGuardCodeRequest
{
    [IpcMaxLength(32)] public required string Code { get; init; }
}

public sealed record AccountNameRequest
{
    [IpcMaxLength(64)] public required string AccountName { get; init; }
}

public sealed record SteamLoginTokenDeleteRequest
{
    [IpcRange(1, int.MaxValue)] public required int Id { get; init; }
}

public sealed record SteamPersonaStateRequest
{
    [IpcMaxLength(64)] public required string AccountName { get; init; }
    [IpcRange(0, 7)] public required int PersonaState { get; init; }
}

public sealed record SteamLoginResultDto
{
    public required bool Success { get; init; }
    [IpcOptional] public string? AccountName { get; init; }
    [IpcOptional] public string? Error { get; init; }
    [IpcOptional] public string? ErrorCode { get; init; }
}

public sealed record SteamLoginTokenDto
{
    public required int Id { get; init; }
    public required string AccountName { get; init; }
    public required int CreatedAt { get; init; }
    [IpcOptional, IpcNumber] public long? ExpiresAt { get; init; }
}

public sealed record SteamLoginEventDto
{
    [IpcStringValues(
        "connecting", "authenticating", "guardCodeNeeded", "deviceConfirmationNeeded", "qrCode",
        "success", "error", "cancelled", "userDisconnected", "userReconnected", "reconnectFailed")]
    public required string Type { get; init; }
    [IpcOptional] public SteamLoginEventDataDto? Data { get; init; }
}

public sealed record SteamLoginEventDataDto
{
    [IpcOptional, IpcStringValues("device", "email")] public string? GuardType { get; init; }
    [IpcOptional] public string? Email { get; init; }
    [IpcOptional] public bool? PreviousCodeWasIncorrect { get; init; }
    [IpcOptional] public string? QrImageBase64 { get; init; }
    [IpcOptional] public string? AccountName { get; init; }
    [IpcOptional] public string? Message { get; init; }
    [IpcOptional] public string? ErrorCode { get; init; }
}

public sealed record SteamFriendInfoRequest
{
    [IpcMaxLength(64)] public required string AccountName { get; init; }
    [IpcMaxLength(20)] public required string FriendSteamId { get; init; }
}

public sealed record SteamFriendsTrackingRequest
{
    [IpcMaxLength(64)] public required string AccountName { get; init; }
    [IpcMaxLength(20)] public required IReadOnlyList<string> FriendSteamIds { get; init; }
}

public sealed record FriendStatusRecordsQueryRequest
{
    [IpcOptional, IpcMaxLength(64)] public string? AccountName { get; init; }
    [IpcOptional, IpcMaxLength(20)] public string? FriendSteamId { get; init; }
    [IpcOptional, IpcMaxLength(32)] public string? ChangeType { get; init; }
    [IpcOptional, IpcNumber] public long? StartTime { get; init; }
    [IpcOptional, IpcNumber] public long? EndTime { get; init; }
    [IpcOptional, IpcRange(1, 1000)] public int? Limit { get; init; }
}

public sealed record FriendStatusRecordsClearRequest
{
    [IpcOptional, IpcMaxLength(64)] public string? AccountName { get; init; }
    [IpcOptional, IpcMaxLength(20)] public string? FriendSteamId { get; init; }
}

public sealed record FriendStatusRecordDto
{
    public required int Id { get; init; }
    public required string AccountName { get; init; }
    public required string FriendSteamId { get; init; }
    public required string FriendPersonaName { get; init; }
    public required string ChangeType { get; init; }
    [IpcOptional] public string? PreviousValue { get; init; }
    [IpcOptional] public string? CurrentValue { get; init; }
    [IpcNumber] public required long Timestamp { get; init; }
}

[IpcTypeName("SteamFriendsUpdateEvent")]
public sealed record SteamFriendsUpdatedEventDto(string AccountName, SteamFriendsDataDto Data);

[IpcTypeName("SteamFriendData")]
public sealed record SteamFriendsDataDto(
    string AccountName,
    SteamFriendDto CurrentUser,
    IReadOnlyList<SteamFriendDto> Friends,
    int LastUpdateTime);

[IpcTypeName("SteamFriendInfo")]
public sealed record SteamFriendDto
{
    public required string SteamId { get; init; }
    public required string PersonaName { get; init; }
    public required int PersonaState { get; init; }
    public required int PersonaStateFlags { get; init; }
    public required int Relationship { get; init; }
    public required string GameName { get; init; }
    public required string GameId { get; init; }
    public required string AvatarHash { get; init; }
    [IpcNumber] public required long LastLogOff { get; init; }
    [IpcNumber] public required long LastLogOn { get; init; }
    public required string RichPresence { get; init; }
    [IpcOptional] public int? Level { get; init; }
}

public sealed record SteamOwnedGameDto
{
    public required int AppId { get; init; }
    public required string Name { get; init; }
    public required string NameLocalized { get; init; }
    public required int PlaytimeForever { get; init; }
    public required int Playtime2Weeks { get; init; }
    public required int RtimeLastPlayed { get; init; }
    public required string ImgIconUrl { get; init; }
    public required bool HasCommunityVisibleStats { get; init; }
    public required IReadOnlyList<int> ContentDescriptorIds { get; init; }
    public required bool IsOwned { get; init; }
    public required bool IsFamilyShared { get; init; }
    public required bool IsInWishlist { get; init; }
    public required IReadOnlyList<string> OwnerSteamIds { get; init; }
    public required IReadOnlyList<string> OwnerNames { get; init; }
    public required int AchievementTotal { get; init; }
    public required int AchievementUnlocked { get; init; }
    public required double AchievementPercentage { get; init; }
}

public sealed record UpdateAppRunningStatusJobStatusDto(
    bool IsRunning,
    [property: IpcNumber] long LastUpdateTime,
    double IntervalTime);

public sealed record AppSettingsDto
{
    public required bool AutoStart { get; init; }
    public required bool SilentStart { get; init; }
    public required bool AutoUpdate { get; init; }
    [IpcStringValues("zh-CN", "en-US")] public required string Language { get; init; }
    [IpcStringValues("exit", "minimize", "ask")] public required string CloseAction { get; init; }
    [IpcStringValues("/status", "/user", "/app", "/useRecord")] public required string HomePage { get; init; }
    [IpcStringValues("light", "dark", "system")] public required string ColorScheme { get; init; }
    public required string ThemeColor { get; init; }
    public required double Radius { get; init; }
    public required double ZoomFactor { get; init; }
    public required bool ExperimentalFeatures { get; init; }
    public required UpdateAppRunningStatusJobSettingsDto UpdateAppRunningStatusJob { get; init; }
}

public sealed record UpdateAppRunningStatusJobSettingsDto(bool Enabled, int IntervalSeconds);

public sealed record AppSettingsPatchDto
{
    [IpcOptional] public bool? AutoStart { get; init; }
    [IpcOptional] public bool? SilentStart { get; init; }
    [IpcOptional] public bool? AutoUpdate { get; init; }
    [IpcOptional, IpcStringValues("zh-CN", "en-US")] public string? Language { get; init; }
    [IpcOptional, IpcStringValues("exit", "minimize", "ask")] public string? CloseAction { get; init; }
    [IpcOptional, IpcStringValues("/status", "/user", "/app", "/useRecord")] public string? HomePage { get; init; }
    [IpcOptional, IpcStringValues("light", "dark", "system")] public string? ColorScheme { get; init; }
    [IpcOptional, IpcMaxLength(64)] public string? ThemeColor { get; init; }
    [IpcOptional, IpcRange(0, 1)] public double? Radius { get; init; }
    [IpcOptional, IpcRange(0.5, 2.5)] public double? ZoomFactor { get; init; }
    [IpcOptional] public bool? ExperimentalFeatures { get; init; }
    [IpcOptional] public UpdateAppRunningStatusJobSettingsPatchDto? UpdateAppRunningStatusJob { get; init; }
}

public sealed record UpdateAppRunningStatusJobSettingsPatchDto
{
    [IpcOptional] public bool? Enabled { get; init; }
    [IpcOptional, IpcRange(1, 86400)] public int? IntervalSeconds { get; init; }
}

public sealed record UpdaterStatusDto(
    bool AutoUpdateEnabled,
    bool IsChecking,
    bool IsDownloading,
    int CheckUpdateInterval,
    string CurrentVersion);

public sealed record UpdaterEventDto
{
    [IpcStringValues(
        "checking-for-update", "update-available", "update-not-available", "download-progress",
        "update-downloaded", "error")]
    public required string UpdaterEvent { get; init; }
    [IpcOptional] public UpdaterEventPayload? Data { get; init; }
}

[IpcUnion(typeof(string), typeof(UpdaterEventDataDto))]
[JsonConverter(typeof(UpdaterEventPayloadJsonConverter))]
public abstract record UpdaterEventPayload;

public sealed record UpdaterVersionEventPayload(string Value) : UpdaterEventPayload;

public sealed record UpdaterDetailsEventPayload(UpdaterEventDataDto Value) : UpdaterEventPayload;

public sealed class UpdaterEventPayloadJsonConverter : JsonConverter<UpdaterEventPayload>
{
    public override UpdaterEventPayload Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException("Updater event payloads are output-only.");

    public override void Write(Utf8JsonWriter writer, UpdaterEventPayload value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case UpdaterVersionEventPayload version:
                writer.WriteStringValue(version.Value);
                break;
            case UpdaterDetailsEventPayload details:
                JsonSerializer.Serialize(writer, details.Value, options);
                break;
            default:
                throw new JsonException($"Unsupported updater event payload: {value.GetType().Name}");
        }
    }
}

public sealed record UpdaterEventDataDto
{
    [IpcOptional] public string? Version { get; init; }
    [IpcOptional] public IReadOnlyList<UpdaterFileDto>? Files { get; init; }
    [IpcOptional] public string? ReleaseDate { get; init; }
    [IpcOptional] public string? ReleaseName { get; init; }
    [IpcOptional] public IReadOnlyList<UpdaterReleaseNoteDto>? ReleaseNotes { get; init; }
    [IpcOptional] public double? StagingPercentage { get; init; }
    [IpcOptional] public string? Progress { get; init; }
    [IpcOptional, IpcNumber] public long? BytesPerSecond { get; init; }
    [IpcOptional] public double? Percent { get; init; }
    [IpcOptional, IpcNumber] public long? Transferred { get; init; }
    [IpcOptional, IpcNumber] public long? Total { get; init; }
    [IpcOptional] public string? Message { get; init; }
}

public sealed record UpdaterFileDto
{
    public required string Url { get; init; }
    public required double Size { get; init; }
    public required double BlockMapSize { get; init; }
    public required string Sha512 { get; init; }
    public required bool IsAdminRightsRequired { get; init; }
}

public sealed record UpdaterReleaseNoteDto(string Version, string Note);
