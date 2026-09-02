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

    /// <summary>
    /// 是否启用实验性功能（Steam 登录 / 好友 / 游戏库等尚未稳定的模块）。
    /// 关闭时相关路由与菜单不会注册。
    /// </summary>
    [JsonPropertyName("experimentalFeatures")]
    public bool? ExperimentalFeatures { get; set; }

    [JsonPropertyName("updateAppRunningStatusJob")]
    public UpdateAppRunningStatusJobSettings? UpdateAppRunningStatusJob { get; set; }
}

public class UpdateAppRunningStatusJobSettings
{
    [JsonPropertyName("enabled")] public bool? Enabled { get; set; }
    [JsonPropertyName("intervalSeconds")] public int? IntervalSeconds { get; set; }
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
        HomePage = "/status",
        ColorScheme = "system",
        ThemeColor = "blue",
        Radius = 0.5,
        ZoomFactor = 1.0,
        ExperimentalFeatures = false,
        UpdateAppRunningStatusJob = new UpdateAppRunningStatusJobSettings
        {
            Enabled = true,
            IntervalSeconds = 5
        }
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
            return await WriteAtomicAsync(settingsFactory.CreateDefaults(), cancellationToken).ConfigureAwait(false);
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
        return new AppSettings
        {
            AutoStart = newSettings.AutoStart ?? oldSettings.AutoStart ?? defaults.AutoStart,
            SilentStart = newSettings.SilentStart ?? oldSettings.SilentStart ?? defaults.SilentStart,
            AutoUpdate = newSettings.AutoUpdate ?? oldSettings.AutoUpdate ?? defaults.AutoUpdate,
            Language = newSettings.Language ?? oldSettings.Language ?? defaults.Language,
            CloseAction = newSettings.CloseAction ?? oldSettings.CloseAction ?? defaults.CloseAction,
            HomePage = newSettings.HomePage ?? oldSettings.HomePage ?? defaults.HomePage,
            ColorScheme = newSettings.ColorScheme ?? oldSettings.ColorScheme ?? defaults.ColorScheme,
            ThemeColor = newSettings.ThemeColor ?? oldSettings.ThemeColor ?? defaults.ThemeColor,
            Radius = newSettings.Radius ?? oldSettings.Radius ?? defaults.Radius,
            ZoomFactor = newSettings.ZoomFactor ?? oldSettings.ZoomFactor ?? defaults.ZoomFactor,
            ExperimentalFeatures = newSettings.ExperimentalFeatures ?? oldSettings.ExperimentalFeatures ?? defaults.ExperimentalFeatures,
            UpdateAppRunningStatusJob = new UpdateAppRunningStatusJobSettings
            {
                Enabled = newSettings.UpdateAppRunningStatusJob?.Enabled ?? oldSettings.UpdateAppRunningStatusJob?.Enabled ?? defaults.UpdateAppRunningStatusJob!.Enabled,
                IntervalSeconds = newSettings.UpdateAppRunningStatusJob?.IntervalSeconds ?? oldSettings.UpdateAppRunningStatusJob?.IntervalSeconds ?? defaults.UpdateAppRunningStatusJob!.IntervalSeconds
            }
        };
    }

    private AppSettings ReadSettings()
    {
        try
        {
            if (File.Exists(appPaths.SettingsFile))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(appPaths.SettingsFile));
                if (settings != null) return Merge(settings);
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
}

public interface IUpdaterController
{
    void SetAutoUpdateEnabled(bool enabled);
}

public interface IAppRunningStatusJobController
{
    void SetEnabled(bool enabled);
    void SetInterval(TimeSpan interval);
}

public sealed class SettingsCoordinator(
    ISettingsStore settingsStore,
    IAutoStartManager autoStartManager,
    IWindowPreferences windowPreferences,
    IUpdaterController updaterController,
    IAppRunningStatusJobController jobController,
    ILogger<SettingsCoordinator> logger) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AppSettings GetSettings() => settingsStore.GetSettings();

    public async Task<bool> UpdateSettingsAsync(object? parameter, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (parameter is not Dictionary<string, object> values) return false;
            var json = JsonSerializer.Serialize(values);
            var partial = JsonSerializer.Deserialize<AppSettings>(json);
            if (partial == null) return false;

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
        updaterController.SetAutoUpdateEnabled(settings.AutoUpdate!.Value);
    }

    private async Task ApplyChangedSideEffectsAsync(AppSettings partial, AppSettings merged, CancellationToken cancellationToken)
    {
        if (partial.AutoStart != null || partial.SilentStart != null)
            await autoStartManager.ApplyAsync(merged.AutoStart!.Value, merged.SilentStart!.Value, cancellationToken).ConfigureAwait(false);
        if (partial.UpdateAppRunningStatusJob?.Enabled != null)
            jobController.SetEnabled(partial.UpdateAppRunningStatusJob.Enabled.Value);
        if (partial.UpdateAppRunningStatusJob?.IntervalSeconds != null)
            jobController.SetInterval(TimeSpan.FromSeconds(partial.UpdateAppRunningStatusJob.IntervalSeconds.Value));
        if (partial.AutoUpdate != null) updaterController.SetAutoUpdateEnabled(partial.AutoUpdate.Value);
        if (partial.ZoomFactor != null)
            windowPreferences.SetZoomFactor(Math.Round(Math.Clamp(partial.ZoomFactor.Value, 0.5, 2.5), 2));
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
        ExperimentalFeatures = newer.ExperimentalFeatures ?? older.ExperimentalFeatures,
        UpdateAppRunningStatusJob = new UpdateAppRunningStatusJobSettings
        {
            Enabled = newer.UpdateAppRunningStatusJob?.Enabled ?? older.UpdateAppRunningStatusJob!.Enabled,
            IntervalSeconds = newer.UpdateAppRunningStatusJob?.IntervalSeconds ?? older.UpdateAppRunningStatusJob!.IntervalSeconds
        }
    };

    public void Dispose() => _gate.Dispose();
}
