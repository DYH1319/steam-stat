using ElectronNet.Models;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Features;
using SteamStat.Core.Features.Achievements;
using SteamStat.Core.Features.Friends;
using SteamStat.Core.Features.Library;
using SteamStat.Core.Features.Login;
using SteamStat.Core.Http;
using SteamStat.Core.Steam;
using SteamStat.Core.Steam.Cache;
using SteamStat.Core.Steam.Session;
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

    internal static SteamOperationalStatusDto ToDto(SteamOperationalStatus value) => new()
    {
        Connectivity = ToCamelCase(value.Connectivity),
        Dependencies = new[]
        {
            ToDto(SteamDependency.CmTransport, value.Dependencies.CmTransport),
            ToDto(SteamDependency.SteamWebApi, value.Dependencies.SteamWebApi),
            ToDto(SteamDependency.Store, value.Dependencies.Store),
            ToDto(SteamDependency.Cdn, value.Dependencies.Cdn),
            ToDto(SteamDependency.Community, value.Dependencies.Community),
            ToDto(SteamDependency.PublicData, value.Dependencies.PublicData),
            ToDto(SteamDependency.Download, value.Dependencies.Download)
        },
        Sessions = value.Sessions.Select(ToDto).ToArray(),
        Resources = value.Resources.Select(ToDto).ToArray(),
        ReauthenticationAccounts = value.ReauthenticationAccounts,
        ChangedAt = value.Dependencies.ChangedAt.ToUnixTimeSeconds()
    };

    private static SteamDependencyHealthDto ToDto(SteamDependency dependency, DependencyHealth value) => new()
    {
        Dependency = ToCamelCase(dependency),
        State = ToCamelCase(value.State),
        LastSuccessAt = value.LastSuccessAt?.ToUnixTimeSeconds(),
        LastFailureAt = value.LastFailureAt?.ToUnixTimeSeconds(),
        FailureKind = value.LastFailureKind?.ToString(),
        IsCircuitOpen = value.IsCircuitOpen,
        IsRateLimited = value.IsRateLimited
    };

    private static SteamSessionStatusDto ToDto(SteamSessionStatusSnapshot value) => new()
    {
        AccountName = value.AccountName,
        State = ToCamelCase(value.State),
        Generation = value.Generation,
        ReconnectAttempt = value.ReconnectAttempt,
        ErrorCode = value.ErrorCode
    };

    private static SteamResourceStatusDto ToDto(SteamResourceStatus value) => new()
    {
        ResourceKind = value.ResourceKind,
        AccountName = value.AccountName,
        Source = value.Source.HasValue ? ToCamelCase(value.Source.Value) : null,
        Freshness = value.Freshness.HasValue ? ToCamelCase(value.Freshness.Value) : null,
        LastSuccessfulUpdate = value.LastSuccessfulUpdate?.ToUnixTimeSeconds(),
        FailureKind = value.Failure?.ToString(),
        DiagnosticCode = value.DiagnosticCode
    };

    internal static SteamLibraryResultDto ToDto(SteamLibraryResult value) => new()
    {
        Status = ToCamelCase(value.Status),
        Libraries = value.Libraries.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<SteamOwnedGameDto>)pair.Value.Select(ToDto).ToArray()),
        Resources = value.Resources.Select(ToDto).ToArray()
    };

    internal static SteamFriendsResultDto ToDto(SteamFriendsResult value) => new()
    {
        Status = ToCamelCase(value.Status),
        Accounts = value.Accounts.Select(account => ToDto(account)!).ToArray(),
        Resources = value.Resources.Select(ToDto).ToArray()
    };

    private static string ToCamelCase<T>(T value) where T : struct, Enum
    {
        var text = value.ToString();
        return $"{char.ToLowerInvariant(text[0])}{text[1..]}";
    }

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

    internal static SteamAchievementOverviewResultDto ToDto(SteamAchievementOverviewResult value) => new()
    {
        Status = "success",
        AccountName = value.AccountName,
        Games = value.Games.Select(ToDto).ToArray(),
        Source = value.ProgressState.Source.HasValue ? ToCamelCase(value.ProgressState.Source.Value) : null,
        Freshness = value.ProgressState.Freshness.HasValue ? ToCamelCase(value.ProgressState.Freshness.Value) : null,
        LastSuccessfulUpdate = value.ProgressState.LastSuccessfulUpdate?.ToUnixTimeSeconds(),
        Partial = value.IsPartial,
        Failure = value.ProgressState.Failure.HasValue ? ToCamelCase(value.ProgressState.Failure.Value) : null,
        DiagnosticCode = value.ProgressState.DiagnosticCode
    };

    private static SteamAchievementOverviewItemDto ToDto(SteamAchievementOverviewItem value) => new()
    {
        AppId = value.AppId,
        Name = value.Name,
        LocalizedName = value.LocalizedName,
        PlaytimeForever = value.PlaytimeForever,
        LastPlayedAt = value.LastPlayedAt,
        Progress = value.Progress == null ? null : ToDto(value.Progress)
    };

    private static SteamAchievementProgressDto ToDto(SteamAchievementAppProgressSnapshot value) => new()
    {
        AppId = value.AppId,
        Total = value.Total,
        Unlocked = value.Unlocked,
        Percentage = value.Percentage
    };

    internal static SteamAchievementGameResultDto ToDto(SteamAchievementGameResult value) => new()
    {
        Status = value.IsSuccess ? "success" : "failure",
        AppId = value.AppId,
        AppName = value.AppName,
        Language = value.Language,
        SchemaHash = value.SchemaHash,
        Achievements = value.Achievements.Select(ToDto).ToArray(),
        Groups = (value.Schema?.Groups ?? []).Select(ToDto).ToArray(),
        Summary = ToDto(value.Summary),
        Source = value.SchemaState.Source.HasValue ? ToCamelCase(value.SchemaState.Source.Value) : null,
        Freshness = value.SchemaState.Freshness.HasValue ? ToCamelCase(value.SchemaState.Freshness.Value) : null,
        LastSuccessfulUpdate = value.SchemaState.LastSuccessfulUpdate?.ToUnixTimeSeconds(),
        Partial = value.IsPartial,
        Stale = value.IsStale,
        Failure = value.SchemaState.Failure.HasValue ? ToCamelCase(value.SchemaState.Failure.Value) : null,
        DiagnosticCode = value.SchemaState.DiagnosticCode,
        ProgressState = ToDto(value.ProgressState)
    };

    private static SteamAchievementDto ToDto(SteamAchievementEntry value) => new()
    {
        InternalKey = value.Definition.InternalKey,
        InternalName = value.Definition.InternalName,
        LocalizedName = value.Definition.LocalizedName,
        LocalizedDescription = value.Definition.LocalizedDescription,
        Icon = value.Definition.Icon,
        IconGray = value.Definition.IconGray,
        Hidden = value.Definition.Hidden,
        GlobalUnlockedPercent = value.Definition.GlobalUnlockedPercent,
        GroupId = value.Definition.GroupId,
        Archived = value.Definition.Archived,
        ProgressType = ToCamelCase(value.Definition.ProgressType),
        MinProgress = value.Definition.MinProgress,
        MaxProgress = value.Definition.MaxProgress,
        IsUnlocked = value.IsUnlocked,
        UnlockTimeUtc = value.UnlockTimeUtc?.ToUnixTimeSeconds(),
        IsRevealed = value.IsRevealed
    };

    private static SteamAchievementGroupDto ToDto(SteamAchievementGroup value) => new()
    {
        GroupId = value.GroupId,
        LocalizedName = value.LocalizedName,
        DlcAppId = value.DlcAppId,
        Archived = value.Archived,
        DeveloperOnly = value.DeveloperOnly,
        IsPublic = value.IsPublic,
        Order = value.Order
    };

    private static SteamAchievementSummaryDto ToDto(SteamAchievementSummary value) => new()
    {
        Total = value.Total,
        Unlocked = value.Unlocked,
        Unknown = value.Unknown,
        Percentage = value.Percentage
    };

    private static SteamAchievementResourceStateDto ToDto(SteamAchievementResourceState value) => new()
    {
        Source = value.Source.HasValue ? ToCamelCase(value.Source.Value) : null,
        Freshness = value.Freshness.HasValue ? ToCamelCase(value.Freshness.Value) : null,
        LastSuccessfulUpdate = value.LastSuccessfulUpdate?.ToUnixTimeSeconds(),
        Failure = value.Failure.HasValue ? ToCamelCase(value.Failure.Value) : null,
        DiagnosticCode = value.DiagnosticCode,
        HasValue = value.HasValue
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
