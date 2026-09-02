using ElectronNet.Models;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Features.Friends;
using SteamStat.Core.Features.Library;
using SteamStat.Core.Features.Login;
using CoreAppSettings = SteamStat.Core.Settings.AppSettings;
using CoreJobSettings = SteamStat.Core.Settings.UpdateAppRunningStatusJobSettings;

namespace ElectronNet.Hosting;

internal static class IpcDtoMapper
{
    internal static GlobalStatusDto? ToDto(GlobalStatus? value) => value == null ? null : new GlobalStatusDto
    {
        Id = value.Id,
        SteamPath = value.SteamPath,
        SteamExePath = value.SteamExePath,
        SteamPid = value.SteamPid,
        SteamClientDllPath = value.SteamClientDllPath,
        SteamClientDll64Path = value.SteamClientDll64Path,
        ActiveUserSteamId = value.ActiveUserSteamId,
        RunningAppId = value.RunningAppId,
        RefreshTime = value.RefreshTime,
        SteamUserRefreshTime = value.SteamUserRefreshTime,
        SteamAppRefreshTime = value.SteamAppRefreshTime
    };

    internal static SteamUserDto ToDto(SteamUser value) => new()
    {
        Id = value.Id,
        SteamId = value.SteamId,
        AccountId = value.AccountId,
        AccountName = value.AccountName,
        PersonaName = value.PersonaName,
        RememberPassword = value.RememberPassword,
        WantsOfflineMode = value.WantsOfflineMode,
        SkipOfflineModeWarning = value.SkipOfflineModeWarning,
        AllowAutoLogin = value.AllowAutoLogin,
        MostRecent = value.MostRecent,
        Timestamp = value.Timestamp,
        AvatarFull = value.AvatarFull,
        AvatarMedium = value.AvatarMedium,
        AvatarSmall = value.AvatarSmall,
        AnimatedAvatar = value.AnimatedAvatar,
        AvatarFrame = value.AvatarFrame,
        Level = value.Level,
        LevelClass = value.LevelClass
    };

    internal static SteamAppDto ToDto(SteamApp value) => new()
    {
        Id = value.Id,
        AppId = value.AppId,
        Name = value.Name,
        NameLocalized = value.NameLocalizedJson,
        Installed = value.Installed,
        InstallDir = value.InstallDir,
        InstallDirPath = value.InstallDirPath,
        AppOnDisk = value.AppOnDisk,
        AppOnDiskReal = value.AppOnDiskReal,
        IsRunning = value.IsRunning,
        Type = value.Type,
        Developer = value.Developer,
        Publisher = value.Publisher,
        SteamReleaseDate = value.SteamReleaseDate,
        IsFreeApp = value.IsFreeApp
    };

    internal static FriendStatusRecordDto ToDto(FriendStatusRecord value) => new()
    {
        Id = value.Id,
        AccountName = value.AccountName,
        FriendSteamId = value.FriendSteamId,
        FriendPersonaName = value.FriendPersonaName,
        ChangeType = value.ChangeType,
        PreviousValue = value.PreviousValue,
        CurrentValue = value.CurrentValue,
        Timestamp = value.Timestamp
    };

    internal static SteamLoginResultDto ToDto(SteamLoginResult value) => new()
    {
        Success = value.Success,
        AccountName = value.AccountName,
        Error = value.Error,
        ErrorCode = value.ErrorCode
    };

    internal static SteamLoginTokenDto ToDto(SteamLoginTokenSummary value) => new()
    {
        Id = value.Id,
        AccountName = value.AccountName,
        CreatedAt = value.CreatedAt,
        ExpiresAt = value.ExpiresAt
    };

    internal static SteamFriendsDataDto? ToDto(SteamFriendData? value) => value == null ? null : new SteamFriendsDataDto(
        value.AccountName,
        ToDto(value.CurrentUser),
        value.Friends.Select(ToDto).ToArray(),
        value.LastUpdateTime);

    internal static SteamFriendDto ToDto(SteamFriendInfo value) => new()
    {
        SteamId = value.SteamId,
        PersonaName = value.PersonaName,
        PersonaState = value.PersonaState,
        PersonaStateFlags = value.PersonaStateFlags,
        Relationship = value.Relationship,
        GameName = value.GameName,
        GameId = value.GameId,
        AvatarHash = value.AvatarHash,
        LastLogOff = value.LastLogOff,
        LastLogOn = value.LastLogOn,
        RichPresence = value.RichPresence,
        Level = value.Level
    };

    internal static SteamOwnedGameDto ToDto(SteamOwnedGame value) => new()
    {
        AppId = value.AppId,
        Name = value.Name,
        NameLocalized = value.NameLocalized,
        PlaytimeForever = value.PlaytimeForever,
        Playtime2Weeks = value.Playtime2Weeks,
        RtimeLastPlayed = value.RtimeLastPlayed,
        ImgIconUrl = value.ImgIconUrl,
        HasCommunityVisibleStats = value.HasCommunityVisibleStats,
        ContentDescriptorIds = value.ContentDescriptorIds,
        IsOwned = value.IsOwned,
        IsFamilyShared = value.IsFamilyShared,
        IsInWishlist = value.IsInWishlist,
        OwnerSteamIds = value.OwnerSteamIds,
        OwnerNames = value.OwnerNames,
        AchievementTotal = value.AchievementTotal,
        AchievementUnlocked = value.AchievementUnlocked,
        AchievementPercentage = value.AchievementPercentage
    };

    internal static AppSettingsDto ToDto(CoreAppSettings value) => new()
    {
        AutoStart = value.AutoStart!.Value,
        SilentStart = value.SilentStart!.Value,
        AutoUpdate = value.AutoUpdate!.Value,
        Language = value.Language!,
        CloseAction = value.CloseAction!,
        HomePage = value.HomePage!,
        ColorScheme = value.ColorScheme!,
        ThemeColor = value.ThemeColor!,
        Radius = value.Radius!.Value,
        ZoomFactor = value.ZoomFactor!.Value,
        ExperimentalFeatures = value.ExperimentalFeatures!.Value,
        UpdateAppRunningStatusJob = new UpdateAppRunningStatusJobSettingsDto(
            value.UpdateAppRunningStatusJob!.Enabled!.Value,
            value.UpdateAppRunningStatusJob.IntervalSeconds!.Value)
    };

    internal static CoreAppSettings ToCore(AppSettingsPatchDto value) => new()
    {
        AutoStart = value.AutoStart,
        SilentStart = value.SilentStart,
        AutoUpdate = value.AutoUpdate,
        Language = value.Language,
        CloseAction = value.CloseAction,
        HomePage = value.HomePage,
        ColorScheme = value.ColorScheme,
        ThemeColor = value.ThemeColor,
        Radius = value.Radius,
        ZoomFactor = value.ZoomFactor,
        ExperimentalFeatures = value.ExperimentalFeatures,
        UpdateAppRunningStatusJob = value.UpdateAppRunningStatusJob == null ? null : new CoreJobSettings
        {
            Enabled = value.UpdateAppRunningStatusJob.Enabled,
            IntervalSeconds = value.UpdateAppRunningStatusJob.IntervalSeconds
        }
    };
}
