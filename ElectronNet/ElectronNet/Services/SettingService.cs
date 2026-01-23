using System.Text.Json;
using ElectronNet.Constants;
using ElectronNet.Models.Settings;

namespace ElectronNet.Services;

public static class SettingService
{
    /// <summary>
    /// 读取设置
    /// </summary>
    public static AppSettings GetSettings()
    {
        try
        {
            var filePath = GetSettingsFilePath();
            if (File.Exists(filePath))
            {
                var data = File.ReadAllText(filePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(data);
                if (settings != null)
                {
                    // 合并默认设置，确保新增的设置项也有默认值
                    return MergeSettings(settings);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} 读取设置失败: {ex.Message}");
        }

        return AppSettings.DefaultSettings;
    }

    /// <summary>
    /// 保存设置
    /// </summary>
    public static bool SaveSettings(AppSettings settings)
    {
        try
        {
            var filePath = GetSettingsFilePath();
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
            Console.WriteLine($"{ConsoleLogPrefix.SETTING} 设置已保存");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} 保存设置失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 重置设置为默认值
    /// </summary>
    public static bool ResetSettings()
    {
        return SaveSettings(AppSettings.DefaultSettings);
    }
    
    /// <summary>
    /// 获取设置文件路径
    /// </summary>
    private static string GetSettingsFilePath()
    {
        var settingsFolderPath = Path.Combine(Program.UserDataPath!, "Settings");

        // 确保设置目录存在
        if (!Directory.Exists(settingsFolderPath))
        {
            Directory.CreateDirectory(settingsFolderPath);
        }

        return Path.Combine(settingsFolderPath, "app-settings.json");
    }

    /// <summary>
    /// 合并设置，新设置覆盖默认设置
    /// </summary>
    private static AppSettings MergeSettings(AppSettings newSettings)
    {
        var defaultSettings = AppSettings.DefaultSettings;

        return new AppSettings
        {
            AutoStart = newSettings.AutoStart ?? defaultSettings.AutoStart,
            SilentStart = newSettings.SilentStart ?? defaultSettings.SilentStart,
            AutoUpdate = newSettings.AutoUpdate ?? defaultSettings.AutoUpdate,
            Language = newSettings.Language ?? defaultSettings.Language,
            CloseAction = newSettings.CloseAction ?? defaultSettings.CloseAction,
            UpdateAppRunningStatusJob = new UpdateAppRunningStatusJob
            {
                Enabled = newSettings.UpdateAppRunningStatusJob?.Enabled ?? defaultSettings.UpdateAppRunningStatusJob!.Enabled,
                IntervalSeconds = newSettings.UpdateAppRunningStatusJob?.IntervalSeconds ?? defaultSettings.UpdateAppRunningStatusJob!.IntervalSeconds
            }
        };
    }
}