using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Environment;

namespace SteamStat.Core.Settings;

public class AppSettings
{
    [JsonPropertyName("autoStart")] public bool? AutoStart { get; set; }
    [JsonPropertyName("silentStart")] public bool? SilentStart { get; set; }
    [JsonPropertyName("autoUpdate")] public bool? AutoUpdate { get; set; }
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonPropertyName("closeAction")] public string? CloseAction { get; set; }
    [JsonPropertyName("homePage")] public string? HomePage { get; set; }
    [JsonPropertyName("colorScheme")] public string? ColorScheme { get; set; }
    [JsonPropertyName("themeColor")] public string? ThemeColor { get; set; }
    [JsonPropertyName("radius")] public double? Radius { get; set; }
    [JsonPropertyName("zoomFactor")] public double? ZoomFactor { get; set; }

    /// <summary>窗口材质：none | mica | acrylic。系统不支持时按 none 处理。</summary>
    [JsonPropertyName("windowMaterial")] public string? WindowMaterial { get; set; }

    /// <summary>
    /// 是否启用实验性功能（Steam 登录 / 好友 / 游戏库等尚未稳定的模块）。
    /// 关闭时相关路由与菜单不会注册。
    /// </summary>
    [JsonPropertyName("experimentalFeatures")]
    public bool? ExperimentalFeatures { get; set; }

    [JsonPropertyName("updateAppRunningStatusJob")]
    public UpdateAppRunningStatusJobSettings? UpdateAppRunningStatusJob { get; set; }

    [JsonPropertyName("sessionTracking")]
    public SessionTrackingSettings? SessionTracking { get; set; }

    [JsonPropertyName("network")]
    public NetworkSettings? Network { get; set; }

    [JsonPropertyName("archive")]
    public ArchiveSettings? Archive { get; set; }

    [JsonPropertyName("analytics")]
    public AnalyticsSettings? Analytics { get; set; }

    [JsonPropertyName("weekly")]
    public WeeklySettings? Weekly { get; set; }

    /// <summary>更新通道：stable（只收正式版）| beta（同时接收测试版）。</summary>
    [JsonPropertyName("updateChannel")]
    public string? UpdateChannel { get; set; }

    [JsonPropertyName("diagnostics")]
    public DiagnosticsSettings? Diagnostics { get; set; }

    /// <summary>
    /// 首次启动引导是否已完成。已有 settings.json 但缺少该字段的视为老用户（已完成）。
    /// </summary>
    [JsonPropertyName("onboardingCompleted")]
    public bool? OnboardingCompleted { get; set; }
}

public class NetworkSettings
{
    /// <summary>system | none | custom</summary>
    [JsonPropertyName("proxyMode")] public string? ProxyMode { get; set; }

    /// <summary>自定义代理地址，形如 http://127.0.0.1:7890 或 socks5://127.0.0.1:1080。</summary>
    [JsonPropertyName("proxyUrl")] public string? ProxyUrl { get; set; }

    /// <summary>国内网络优化：CM 只走 WebSocket、放宽超时、渲染层图片优先 Akamai CDN。</summary>
    [JsonPropertyName("cnOptimized")] public bool? CnOptimized { get; set; }
}

/// <summary>
/// 档案扩展：存档备份（只读快照，绝不写回）与截图索引。
/// </summary>
public class ArchiveSettings
{
    [JsonPropertyName("saveBackupEnabled")] public bool? SaveBackupEnabled { get; set; }

    /// <summary>定时快照间隔（小时，1~168）。</summary>
    [JsonPropertyName("saveBackupIntervalHours")] public int? SaveBackupIntervalHours { get; set; }

    /// <summary>游戏会话结束后自动快照该游戏的存档。</summary>
    [JsonPropertyName("saveBackupOnSessionEnd")] public bool? SaveBackupOnSessionEnd { get; set; }

    /// <summary>单个存档目录的体积上限（MB），超出时跳过并提示。</summary>
    [JsonPropertyName("saveBackupMaxTargetMb")] public int? SaveBackupMaxTargetMb { get; set; }

    /// <summary>每个目录保留的版本数，0 = 永久保留全部版本。</summary>
    [JsonPropertyName("saveBackupKeepVersions")] public int? SaveBackupKeepVersions { get; set; }

    [JsonPropertyName("screenshotIndexEnabled")] public bool? ScreenshotIndexEnabled { get; set; }

    /// <summary>启动快照保留份数（1~30）。</summary>
    [JsonPropertyName("startupSnapshotKeep")] public int? StartupSnapshotKeep { get; set; }

    /// <summary>新年后自动为上一年生成封存包。</summary>
    [JsonPropertyName("annualSealEnabled")] public bool? AnnualSealEnabled { get; set; }
}

/// <summary>
/// 统计口径。语义视图在 SQLite 里计算逻辑日、剔除隐藏游戏，所以这些设置必须在 Host。
/// </summary>
public class AnalyticsSettings
{
    /// <summary>0~6：该小时之前的时间计入前一天。</summary>
    [JsonPropertyName("dayBoundaryHour")] public int? DayBoundaryHour { get; set; }

    /// <summary>0 = 周日，1 = 周一。</summary>
    [JsonPropertyName("weekStart")] public int? WeekStart { get; set; }

    /// <summary>隐藏游戏：不出现在统计、报告和任何对外查询里，原始会话保留。</summary>
    [JsonPropertyName("hiddenAppIds")] public List<int>? HiddenAppIds { get; set; }

    /// <summary>渲染层是否已把旧版本保存在本地的这些偏好交给 Host（只发生一次）。</summary>
    [JsonPropertyName("rendererMigrated")] public bool? RendererMigrated { get; set; }

    /// <summary>没有手动状态时，超过这么多天未启动算暂停，也是回坑局的判定线。</summary>
    [JsonPropertyName("pauseDays")] public int? PauseDays { get; set; }

    /// <summary>没有手动状态时，超过这么多天未启动算弃坑。</summary>
    [JsonPropertyName("abandonDays")] public int? AbandonDays { get; set; }
}

/// <summary>档案周刊。</summary>
public class WeeklySettings
{
    [JsonPropertyName("enabled")] public bool? Enabled { get; set; }

    /// <summary>open：每周首次打开时翻开。notify：只发一条通知。off：静静放进过刊馆。</summary>
    [JsonPropertyName("delivery")] public string? Delivery { get; set; }

    /// <summary>全成就、破纪录、累计时长里程碑时立即出号外。</summary>
    [JsonPropertyName("extraEnabled")] public bool? ExtraEnabled { get; set; }

    /// <summary>周刊覆盖哪些账号，空表示全部。本机可能有家人的账号，所以可以选。</summary>
    [JsonPropertyName("steamIds")] public List<string>? SteamIds { get; set; }

    /// <summary>号外的基线（Unix 秒）：这之前发生的事不再出号外。第一次运行时写入。</summary>
    [JsonPropertyName("baselineAt")] public long? BaselineAt { get; set; }
}

public static class WeeklyDelivery
{
    public const string Open = "open";
    public const string Notify = "notify";
    public const string Off = "off";

    public static bool IsValid(string? value) => value is Open or Notify or Off;
}

/// <summary>反馈与诊断。</summary>
public class DiagnosticsSettings
{
    /// <summary>崩溃报告：默认关闭。开启后只在本机记录脱敏的异常堆栈，由用户确认后提交。</summary>
    [JsonPropertyName("crashReportsEnabled")] public bool? CrashReportsEnabled { get; set; }
}

public static class UpdateChannels
{
    public const string Stable = "stable";
    public const string Beta = "beta";

    public static bool IsValid(string? channel) => channel is Stable or Beta;

    /// <summary>未知值按当前版本推断：测试版构建默认留在 beta 通道，否则为 stable。</summary>
    public static string Normalize(string? channel, string? appVersion = null)
        => channel is Stable or Beta ? channel : DefaultFor(appVersion);

    public static string DefaultFor(string? appVersion)
        => appVersion != null && appVersion.Contains('-') ? Beta : Stable;
}

public class UpdateAppRunningStatusJobSettings
{
    [JsonPropertyName("enabled")] public bool? Enabled { get; set; }
    [JsonPropertyName("intervalSeconds")] public int? IntervalSeconds { get; set; }
}

public class SessionTrackingSettings
{
    [JsonPropertyName("afkEnabled")] public bool? AfkEnabled { get; set; }
    [JsonPropertyName("afkThresholdMinutes")] public int? AfkThresholdMinutes { get; set; }
    [JsonPropertyName("minDurationSeconds")] public int? MinDurationSeconds { get; set; }
    [JsonPropertyName("mergeGapSeconds")] public int? MergeGapSeconds { get; set; }
    [JsonPropertyName("excludedAppIds")] public List<int>? ExcludedAppIds { get; set; }
}

public interface IAppSettingsFactory
{
    AppSettings CreateDefaults();
}

public sealed class AppSettingsFactory(AppEnvironment environment) : IAppSettingsFactory
{
    public AppSettings CreateDefaults() => new()
    {
        AutoStart = false,
        SilentStart = false,
        AutoUpdate = true,
        Language = environment.Locale,
        CloseAction = "ask",
        HomePage = "/dashboard",
        ColorScheme = "system",
        ThemeColor = "blue",
        Radius = 0.5,
        ZoomFactor = 1.0,
        WindowMaterial = "none",
        ExperimentalFeatures = false,
        UpdateAppRunningStatusJob = new UpdateAppRunningStatusJobSettings
        {
            Enabled = true,
            IntervalSeconds = 5
        },
        SessionTracking = new SessionTrackingSettings
        {
            AfkEnabled = true,
            AfkThresholdMinutes = 10,
            MinDurationSeconds = 0,
            MergeGapSeconds = 0,
            ExcludedAppIds = []
        },
        Network = new NetworkSettings
        {
            ProxyMode = "system",
            ProxyUrl = "",
            CnOptimized = false
        },
        Archive = new ArchiveSettings
        {
            // 存档备份需要用户主动开启（会复制存档到备份库）
            SaveBackupEnabled = false,
            SaveBackupIntervalHours = 6,
            SaveBackupOnSessionEnd = true,
            SaveBackupMaxTargetMb = 1024,
            SaveBackupKeepVersions = 0,
            ScreenshotIndexEnabled = true,
            StartupSnapshotKeep = 7,
            AnnualSealEnabled = true
        },
        Analytics = new AnalyticsSettings
        {
            DayBoundaryHour = 4,
            WeekStart = 1,
            HiddenAppIds = [],
            RendererMigrated = false,
            PauseDays = 30,
            AbandonDays = 90
        },
        Weekly = new WeeklySettings
        {
            Enabled = true,
            Delivery = WeeklyDelivery.Open,
            ExtraEnabled = true,
            SteamIds = []
        },
        UpdateChannel = UpdateChannels.DefaultFor(environment.AppVersion),
        Diagnostics = new DiagnosticsSettings
        {
            CrashReportsEnabled = false
        },
        OnboardingCompleted = false
    };
}

public interface ISettingsStore
{
    AppSettings GetSettings();
    Task<bool> UpdateAsync(AppSettings partialSettings, CancellationToken cancellationToken = default);
    Task<bool> ResetAsync(CancellationToken cancellationToken = default);
}

public sealed class JsonSettingsStore(
    IAppPaths appPaths,
    IAppSettingsFactory settingsFactory,
    ILogger<JsonSettingsStore> logger) : ISettingsStore, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AppSettings GetSettings()
    {
        _gate.Wait();
        try
        {
            return ReadSettings();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> UpdateAsync(AppSettings partialSettings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(partialSettings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await WriteAtomicAsync(Merge(partialSettings, ReadSettings()), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ResetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 隐藏游戏是隐私名单不是偏好：恢复默认设置不能让它们重新出现在统计和对外查询里
            var defaults = settingsFactory.CreateDefaults();
            var current = ReadSettings();
            defaults.Analytics!.HiddenAppIds = current.Analytics?.HiddenAppIds ?? [];
            defaults.Analytics.RendererMigrated = current.Analytics?.RendererMigrated ?? false;
            // 号外基线不是偏好：清掉它会把恢复默认设置之前的事再报一遍
            defaults.Weekly!.BaselineAt = current.Weekly?.BaselineAt;
            return await WriteAtomicAsync(defaults, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal AppSettings Merge(AppSettings newSettings, AppSettings? oldSettings = null)
    {
        oldSettings ??= settingsFactory.CreateDefaults();
        var defaults = settingsFactory.CreateDefaults();
        var pauseDays = Math.Clamp(newSettings.Analytics?.PauseDays ?? oldSettings.Analytics?.PauseDays ?? defaults.Analytics!.PauseDays!.Value, 1, 3650);
        return new AppSettings
        {
            AutoStart = newSettings.AutoStart ?? oldSettings.AutoStart ?? defaults.AutoStart,
            SilentStart = newSettings.SilentStart ?? oldSettings.SilentStart ?? defaults.SilentStart,
            AutoUpdate = newSettings.AutoUpdate ?? oldSettings.AutoUpdate ?? defaults.AutoUpdate,
            Language = newSettings.Language ?? oldSettings.Language ?? defaults.Language,
            CloseAction = newSettings.CloseAction ?? oldSettings.CloseAction ?? defaults.CloseAction,
            HomePage = NormalizeHomePage(newSettings.HomePage) ?? NormalizeHomePage(oldSettings.HomePage) ?? defaults.HomePage,
            ColorScheme = newSettings.ColorScheme ?? oldSettings.ColorScheme ?? defaults.ColorScheme,
            ThemeColor = newSettings.ThemeColor ?? oldSettings.ThemeColor ?? defaults.ThemeColor,
            Radius = newSettings.Radius ?? oldSettings.Radius ?? defaults.Radius,
            ZoomFactor = newSettings.ZoomFactor ?? oldSettings.ZoomFactor ?? defaults.ZoomFactor,
            WindowMaterial = newSettings.WindowMaterial ?? oldSettings.WindowMaterial ?? defaults.WindowMaterial,
            ExperimentalFeatures = newSettings.ExperimentalFeatures ?? oldSettings.ExperimentalFeatures ?? defaults.ExperimentalFeatures,
            UpdateAppRunningStatusJob = new UpdateAppRunningStatusJobSettings
            {
                Enabled = newSettings.UpdateAppRunningStatusJob?.Enabled ?? oldSettings.UpdateAppRunningStatusJob?.Enabled ?? defaults.UpdateAppRunningStatusJob!.Enabled,
                IntervalSeconds = newSettings.UpdateAppRunningStatusJob?.IntervalSeconds ?? oldSettings.UpdateAppRunningStatusJob?.IntervalSeconds ?? defaults.UpdateAppRunningStatusJob!.IntervalSeconds
            },
            SessionTracking = new SessionTrackingSettings
            {
                AfkEnabled = newSettings.SessionTracking?.AfkEnabled ?? oldSettings.SessionTracking?.AfkEnabled ?? defaults.SessionTracking!.AfkEnabled,
                AfkThresholdMinutes = newSettings.SessionTracking?.AfkThresholdMinutes ?? oldSettings.SessionTracking?.AfkThresholdMinutes ?? defaults.SessionTracking!.AfkThresholdMinutes,
                MinDurationSeconds = newSettings.SessionTracking?.MinDurationSeconds ?? oldSettings.SessionTracking?.MinDurationSeconds ?? defaults.SessionTracking!.MinDurationSeconds,
                MergeGapSeconds = newSettings.SessionTracking?.MergeGapSeconds ?? oldSettings.SessionTracking?.MergeGapSeconds ?? defaults.SessionTracking!.MergeGapSeconds,
                ExcludedAppIds = newSettings.SessionTracking?.ExcludedAppIds ?? oldSettings.SessionTracking?.ExcludedAppIds ?? defaults.SessionTracking!.ExcludedAppIds
            },
            Network = new NetworkSettings
            {
                ProxyMode = newSettings.Network?.ProxyMode ?? oldSettings.Network?.ProxyMode ?? defaults.Network!.ProxyMode,
                ProxyUrl = newSettings.Network?.ProxyUrl ?? oldSettings.Network?.ProxyUrl ?? defaults.Network!.ProxyUrl,
                CnOptimized = newSettings.Network?.CnOptimized ?? oldSettings.Network?.CnOptimized ?? defaults.Network!.CnOptimized
            },
            Archive = new ArchiveSettings
            {
                SaveBackupEnabled = newSettings.Archive?.SaveBackupEnabled ?? oldSettings.Archive?.SaveBackupEnabled ?? defaults.Archive!.SaveBackupEnabled,
                SaveBackupIntervalHours = newSettings.Archive?.SaveBackupIntervalHours ?? oldSettings.Archive?.SaveBackupIntervalHours ?? defaults.Archive!.SaveBackupIntervalHours,
                SaveBackupOnSessionEnd = newSettings.Archive?.SaveBackupOnSessionEnd ?? oldSettings.Archive?.SaveBackupOnSessionEnd ?? defaults.Archive!.SaveBackupOnSessionEnd,
                SaveBackupMaxTargetMb = newSettings.Archive?.SaveBackupMaxTargetMb ?? oldSettings.Archive?.SaveBackupMaxTargetMb ?? defaults.Archive!.SaveBackupMaxTargetMb,
                SaveBackupKeepVersions = newSettings.Archive?.SaveBackupKeepVersions ?? oldSettings.Archive?.SaveBackupKeepVersions ?? defaults.Archive!.SaveBackupKeepVersions,
                ScreenshotIndexEnabled = newSettings.Archive?.ScreenshotIndexEnabled ?? oldSettings.Archive?.ScreenshotIndexEnabled ?? defaults.Archive!.ScreenshotIndexEnabled,
                StartupSnapshotKeep = Math.Clamp(newSettings.Archive?.StartupSnapshotKeep ?? oldSettings.Archive?.StartupSnapshotKeep ?? defaults.Archive!.StartupSnapshotKeep!.Value, 1, 30),
                AnnualSealEnabled = newSettings.Archive?.AnnualSealEnabled ?? oldSettings.Archive?.AnnualSealEnabled ?? defaults.Archive!.AnnualSealEnabled
            },
            Analytics = new AnalyticsSettings
            {
                DayBoundaryHour = Math.Clamp(newSettings.Analytics?.DayBoundaryHour ?? oldSettings.Analytics?.DayBoundaryHour ?? defaults.Analytics!.DayBoundaryHour!.Value, 0, 6),
                WeekStart = (newSettings.Analytics?.WeekStart ?? oldSettings.Analytics?.WeekStart ?? defaults.Analytics!.WeekStart) == 0 ? 0 : 1,
                HiddenAppIds = (newSettings.Analytics?.HiddenAppIds ?? oldSettings.Analytics?.HiddenAppIds ?? defaults.Analytics!.HiddenAppIds!).Distinct().Order().ToList(),
                RendererMigrated = newSettings.Analytics?.RendererMigrated ?? oldSettings.Analytics?.RendererMigrated ?? defaults.Analytics!.RendererMigrated,
                PauseDays = pauseDays,
                AbandonDays = Math.Max(pauseDays + 1, Math.Clamp(newSettings.Analytics?.AbandonDays ?? oldSettings.Analytics?.AbandonDays ?? defaults.Analytics!.AbandonDays!.Value, 2, 3650))
            },
            Weekly = new WeeklySettings
            {
                Enabled = newSettings.Weekly?.Enabled ?? oldSettings.Weekly?.Enabled ?? defaults.Weekly!.Enabled,
                Delivery = WeeklyDelivery.IsValid(newSettings.Weekly?.Delivery) ? newSettings.Weekly!.Delivery
                    : WeeklyDelivery.IsValid(oldSettings.Weekly?.Delivery) ? oldSettings.Weekly!.Delivery
                    : defaults.Weekly!.Delivery,
                ExtraEnabled = newSettings.Weekly?.ExtraEnabled ?? oldSettings.Weekly?.ExtraEnabled ?? defaults.Weekly!.ExtraEnabled,
                SteamIds = (newSettings.Weekly?.SteamIds ?? oldSettings.Weekly?.SteamIds ?? defaults.Weekly!.SteamIds!).Distinct().Order(StringComparer.Ordinal).ToList(),
                BaselineAt = newSettings.Weekly?.BaselineAt ?? oldSettings.Weekly?.BaselineAt
            },
            UpdateChannel = UpdateChannels.IsValid(newSettings.UpdateChannel) ? newSettings.UpdateChannel
                : UpdateChannels.IsValid(oldSettings.UpdateChannel) ? oldSettings.UpdateChannel
                : defaults.UpdateChannel,
            Diagnostics = new DiagnosticsSettings
            {
                CrashReportsEnabled = newSettings.Diagnostics?.CrashReportsEnabled ?? oldSettings.Diagnostics?.CrashReportsEnabled ?? defaults.Diagnostics!.CrashReportsEnabled
            },
            OnboardingCompleted = newSettings.OnboardingCompleted ?? oldSettings.OnboardingCompleted ?? defaults.OnboardingCompleted
        };
    }

    private static string? NormalizeHomePage(string? homePage)
        => homePage == "/status" ? "/data-source/local" : homePage;

    private AppSettings ReadSettings()
    {
        try
        {
            if (File.Exists(appPaths.SettingsFile))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(appPaths.SettingsFile));
                if (settings != null)
                {
                    // 引导功能上线前就存在的设置文件：老用户不再弹首次启动引导
                    settings.OnboardingCompleted ??= true;
                    return Merge(settings);
                }
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to read application settings");
        }
        return settingsFactory.CreateDefaults();
    }

    private async Task<bool> WriteAtomicAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var temporaryFile = appPaths.SettingsFile + ".tmp";
        try
        {
            Directory.CreateDirectory(appPaths.SettingsDirectory);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(temporaryFile, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryFile, appPaths.SettingsFile, overwrite: true);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to save application settings");
            try
            {
                if (File.Exists(temporaryFile)) File.Delete(temporaryFile);
            }
            catch (Exception cleanupException)
            {
                logger.LogWarning(cleanupException, "Failed to remove temporary settings file");
            }
            return false;
        }
    }

    public void Dispose() => _gate.Dispose();
}

public interface IAutoStartManager
{
    Task ApplyAsync(bool autoStart, bool silentStart, CancellationToken cancellationToken = default);
}

public interface IWindowPreferences
{
    void SetZoomFactor(double zoomFactor);
    void SetWindowMaterial(string material);
}

public interface IUpdaterController
{
    void SetAutoUpdateEnabled(bool enabled);
    void SetUpdateChannel(string channel);
}

public interface ICrashReportController
{
    void SetEnabled(bool enabled);
}

public interface IAppRunningStatusJobController
{
    void SetEnabled(bool enabled);
    void SetInterval(TimeSpan interval);
    void SetAfkDetection(bool enabled, TimeSpan threshold);
}

public sealed class SettingsCoordinator(
    ISettingsStore settingsStore,
    IAutoStartManager autoStartManager,
    IWindowPreferences windowPreferences,
    IUpdaterController updaterController,
    IAppRunningStatusJobController jobController,
    SteamStat.Core.Http.ISteamNetworkController networkController,
    ICrashReportController crashReportController,
    ILogger<SettingsCoordinator> logger) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AppSettings GetSettings() => settingsStore.GetSettings();

    public async Task<bool> UpdateSettingsAsync(AppSettings partial, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(partial);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = settingsStore.GetSettings();
            var merged = Merge(partial, current);
            await ApplyChangedSideEffectsAsync(partial, merged, cancellationToken).ConfigureAwait(false);
            return await settingsStore.UpdateAsync(partial, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to update application settings");
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var settings = settingsStore.GetSettings();
        await autoStartManager.ApplyAsync(settings.AutoStart!.Value, settings.SilentStart!.Value, cancellationToken).ConfigureAwait(false);
        jobController.SetInterval(TimeSpan.FromSeconds(settings.UpdateAppRunningStatusJob!.IntervalSeconds!.Value));
        jobController.SetEnabled(settings.UpdateAppRunningStatusJob.Enabled!.Value);
        jobController.SetAfkDetection(
            settings.SessionTracking!.AfkEnabled!.Value,
            TimeSpan.FromMinutes(settings.SessionTracking.AfkThresholdMinutes!.Value));
        updaterController.SetUpdateChannel(settings.UpdateChannel!);
        updaterController.SetAutoUpdateEnabled(settings.AutoUpdate!.Value);
        crashReportController.SetEnabled(settings.Diagnostics!.CrashReportsEnabled!.Value);
        networkController.Apply(settings.Network);
    }

    private async Task ApplyChangedSideEffectsAsync(AppSettings partial, AppSettings merged, CancellationToken cancellationToken)
    {
        if (partial.AutoStart != null || partial.SilentStart != null)
            await autoStartManager.ApplyAsync(merged.AutoStart!.Value, merged.SilentStart!.Value, cancellationToken).ConfigureAwait(false);
        if (partial.UpdateAppRunningStatusJob?.Enabled != null)
            jobController.SetEnabled(partial.UpdateAppRunningStatusJob.Enabled.Value);
        if (partial.UpdateAppRunningStatusJob?.IntervalSeconds != null)
            jobController.SetInterval(TimeSpan.FromSeconds(partial.UpdateAppRunningStatusJob.IntervalSeconds.Value));
        if (partial.SessionTracking?.AfkEnabled != null || partial.SessionTracking?.AfkThresholdMinutes != null)
            jobController.SetAfkDetection(
                merged.SessionTracking!.AfkEnabled!.Value,
                TimeSpan.FromMinutes(merged.SessionTracking.AfkThresholdMinutes!.Value));
        if (partial.UpdateChannel != null) updaterController.SetUpdateChannel(UpdateChannels.Normalize(partial.UpdateChannel));
        if (partial.AutoUpdate != null) updaterController.SetAutoUpdateEnabled(partial.AutoUpdate.Value);
        if (partial.Diagnostics?.CrashReportsEnabled != null) crashReportController.SetEnabled(partial.Diagnostics.CrashReportsEnabled.Value);
        if (partial.ZoomFactor != null)
            windowPreferences.SetZoomFactor(Math.Round(Math.Clamp(partial.ZoomFactor.Value, 0.5, 2.5), 2));
        if (partial.WindowMaterial != null) windowPreferences.SetWindowMaterial(partial.WindowMaterial);
        if (partial.Network != null) networkController.Apply(merged.Network);
    }

    private static AppSettings Merge(AppSettings newer, AppSettings older) => new()
    {
        AutoStart = newer.AutoStart ?? older.AutoStart,
        SilentStart = newer.SilentStart ?? older.SilentStart,
        AutoUpdate = newer.AutoUpdate ?? older.AutoUpdate,
        Language = newer.Language ?? older.Language,
        CloseAction = newer.CloseAction ?? older.CloseAction,
        HomePage = newer.HomePage ?? older.HomePage,
        ColorScheme = newer.ColorScheme ?? older.ColorScheme,
        ThemeColor = newer.ThemeColor ?? older.ThemeColor,
        Radius = newer.Radius ?? older.Radius,
        ZoomFactor = newer.ZoomFactor ?? older.ZoomFactor,
        WindowMaterial = newer.WindowMaterial ?? older.WindowMaterial,
        ExperimentalFeatures = newer.ExperimentalFeatures ?? older.ExperimentalFeatures,
        UpdateAppRunningStatusJob = new UpdateAppRunningStatusJobSettings
        {
            Enabled = newer.UpdateAppRunningStatusJob?.Enabled ?? older.UpdateAppRunningStatusJob!.Enabled,
            IntervalSeconds = newer.UpdateAppRunningStatusJob?.IntervalSeconds ?? older.UpdateAppRunningStatusJob!.IntervalSeconds
        },
        SessionTracking = new SessionTrackingSettings
        {
            AfkEnabled = newer.SessionTracking?.AfkEnabled ?? older.SessionTracking!.AfkEnabled,
            AfkThresholdMinutes = newer.SessionTracking?.AfkThresholdMinutes ?? older.SessionTracking!.AfkThresholdMinutes,
            MinDurationSeconds = newer.SessionTracking?.MinDurationSeconds ?? older.SessionTracking!.MinDurationSeconds,
            MergeGapSeconds = newer.SessionTracking?.MergeGapSeconds ?? older.SessionTracking!.MergeGapSeconds,
            ExcludedAppIds = newer.SessionTracking?.ExcludedAppIds ?? older.SessionTracking!.ExcludedAppIds
        },
        Network = new NetworkSettings
        {
            ProxyMode = newer.Network?.ProxyMode ?? older.Network!.ProxyMode,
            ProxyUrl = newer.Network?.ProxyUrl ?? older.Network!.ProxyUrl,
            CnOptimized = newer.Network?.CnOptimized ?? older.Network!.CnOptimized
        },
        OnboardingCompleted = newer.OnboardingCompleted ?? older.OnboardingCompleted
    };

    public void Dispose() => _gate.Dispose();
}
