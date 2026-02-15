using System.Text.Json;
using ElectronNET.API;
using ElectronNET.API.Entities;
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
    /// 增量更新设置
    /// </summary>
    public static async Task<bool> UpdateSettings(object param)
    {
        try
        {
            var pd = param as Dictionary<string, object>;
            if (pd == null) return false;

            // 转换为 JSON，再反序列化为 AppSettings
            var json = JsonSerializer.Serialize(pd);
            var partialSettings = JsonSerializer.Deserialize<AppSettings>(json);
            if (partialSettings == null) return false;

            // 合并设置
            var currentSettings = GetSettings();
            var mergedSettings = MergeSettings(partialSettings, currentSettings);

            #region 特殊设置项处理

            // 更新开机自启 / 静默启动
            if (partialSettings.AutoStart != null || partialSettings.SilentStart != null)
            {
                if (Program.IsDev)
                {
                    Console.WriteLine($"{ConsoleLogPrefix.WARN} Skip set auto start because application is not packed");
                }
                else
                {
                    Electron.App.SetLoginItemSettings(new LoginSettings
                    {
                        OpenAtLogin = mergedSettings.AutoStart!.Value,
                        Path = (await Electron.App.GetPathAsync(PathName.Exe)).Replace(@"\electron", ""),
                        Args = mergedSettings.SilentStart!.Value ? ["--silent-start"] : []
                    });
                }
            }
            // 更新定时更新应用运行状态任务
            if (partialSettings.UpdateAppRunningStatusJob != null)
            {
                if (partialSettings.UpdateAppRunningStatusJob.Enabled != null)
                {
                    if (partialSettings.UpdateAppRunningStatusJob.Enabled.Value)
                    {
                        Jobs.UpdateAppRunningStatusJob.Start();
                    }
                    else
                    {
                        Jobs.UpdateAppRunningStatusJob.Stop();
                    }
                }
                if (partialSettings.UpdateAppRunningStatusJob.IntervalSeconds != null)
                {
                    Jobs.UpdateAppRunningStatusJob.SetInterval(TimeSpan.FromSeconds(partialSettings.UpdateAppRunningStatusJob.IntervalSeconds.Value));
                }
            }
            // 更新自动更新
            if (partialSettings.AutoUpdate != null)
            {
                UpdateService.AutoUpdateEnabled = partialSettings.AutoUpdate.Value;
            }

            #endregion

            // 保存设置到文件
            return SaveSettings(mergedSettings);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} 更新设置失败: {ex.Message}");
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
    /// 全量保存设置到文件
    /// </summary>
    private static bool SaveSettings(AppSettings settings)
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
    /// 合并设置，新设置覆盖旧设置（若不传递旧设置，则旧设置为默认设置）
    /// </summary>
    private static AppSettings MergeSettings(AppSettings newSettings, AppSettings? oldSettings = null)
    {
        oldSettings ??= AppSettings.DefaultSettings;

        return new AppSettings
        {
            AutoStart = newSettings.AutoStart ?? oldSettings.AutoStart,
            SilentStart = newSettings.SilentStart ?? oldSettings.SilentStart,
            AutoUpdate = newSettings.AutoUpdate ?? oldSettings.AutoUpdate,
            Language = newSettings.Language ?? oldSettings.Language,
            CloseAction = newSettings.CloseAction ?? oldSettings.CloseAction,
            HomePage = newSettings.HomePage ?? oldSettings.HomePage,
            ColorScheme = newSettings.ColorScheme ?? oldSettings.ColorScheme,
            UpdateAppRunningStatusJob = new UpdateAppRunningStatusJob
            {
                Enabled = newSettings.UpdateAppRunningStatusJob?.Enabled ?? oldSettings.UpdateAppRunningStatusJob!.Enabled,
                IntervalSeconds = newSettings.UpdateAppRunningStatusJob?.IntervalSeconds ?? oldSettings.UpdateAppRunningStatusJob!.IntervalSeconds
            }
        };
    }
}
