using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SteamStat.Core.Platform;

namespace SteamStat.Platform.Windows;

[SupportedOSPlatform("windows")]
internal sealed class SteamInstallLocator(ILogger<SteamInstallLocator> logger) : ISteamInstallLocator
{
    internal const string SteamRegistryPath = @"Software\Valve\Steam";
    private const string ActiveProcessPath = @"Software\Valve\Steam\ActiveProcess";
    private const string AppsPath = @"Software\Valve\Steam\Apps";

    public SteamRegistrySnapshot ReadSteamRegistry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SteamRegistryPath);
        if (key == null) return new SteamRegistrySnapshot();

        try
        {
            return new SteamRegistrySnapshot
            {
                AlreadyRetriedOfflineMode = Read<int>(key, "AlreadyRetriedOfflineMode"),
                AutoLoginUser = Read<string>(key, "AutoLoginUser") ?? string.Empty,
                AutoLoginUserSteamChina = Read<string>(key, "AutoLoginUser_steamchina") ?? string.Empty,
                CompletedOOBEStage1 = Read<int>(key, "CompletedOOBEStage1"),
                Language = Read<string>(key, "Language") ?? string.Empty,
                LastGameNameUsed = Read<string>(key, "LastGameNameUsed") ?? string.Empty,
                PseudoUUID = Read<string>(key, "PseudoUUID") ?? string.Empty,
                Rate = Read<string>(key, "Rate") ?? string.Empty,
                RememberPassword = Read<int>(key, "RememberPassword"),
                Restart = Read<int>(key, "Restart"),
                RunningAppID = Read<int>(key, "RunningAppID"),
                Skin = Read<string>(key, "Skin") ?? string.Empty,
                SourceModInstallPath = Read<string>(key, "SourceModInstallPath") ?? string.Empty,
                StartupModeTmp = Read<int>(key, "StartupModeTmp"),
                StartupModeTmpIsValid = Read<int>(key, "StartupModeTmpIsValid"),
                SteamExe = Read<string>(key, "SteamExe") ?? string.Empty,
                SteamPath = Read<string>(key, "SteamPath") ?? string.Empty,
                SuppressAutoRun = Read<int>(key, "SuppressAutoRun")
            };
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to read Steam registry values");
            throw;
        }
    }

    public SteamActiveProcessSnapshot ReadActiveProcess()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ActiveProcessPath);
        if (key == null) return new SteamActiveProcessSnapshot(0, 0, string.Empty, string.Empty, string.Empty);

        try
        {
            return new SteamActiveProcessSnapshot(
                Read<int>(key, "ActiveUser"),
                Read<int>(key, "pid"),
                Read<string>(key, "SteamClientDll") ?? string.Empty,
                Read<string>(key, "SteamClientDll64") ?? string.Empty,
                Read<string>(key, "Universe") ?? string.Empty);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to read Steam active process registry values");
            throw;
        }
    }

    public IReadOnlyDictionary<int, SteamAppRegistrySnapshot> ReadAppRegistry()
    {
        var result = new Dictionary<int, SteamAppRegistrySnapshot>();
        using var root = Registry.CurrentUser.OpenSubKey(AppsPath);
        if (root == null) return result;

        try
        {
            foreach (var name in root.GetSubKeyNames())
            {
                if (!int.TryParse(name, out var appId)) continue;
                using var key = root.OpenSubKey(name);
                if (key == null) continue;
                result[appId] = new SteamAppRegistrySnapshot(
                    appId,
                    Read<int?>(key, "firewall"),
                    Read<int?>(key, "FmSysInfo"),
                    Read<int?>(key, "Cloud"),
                    Read<int?>(key, "Installed"),
                    Read<string?>(key, "Name"),
                    Read<int?>(key, "Running"),
                    Read<int?>(key, "Updating"));
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to read Steam application registry values");
            throw;
        }

        return result;
    }

    public void SetAutoLoginUser(string accountName, bool rememberPassword)
    {
        using var key = Registry.CurrentUser.OpenSubKey(SteamRegistryPath, writable: true);
        if (key == null) return;
        key.SetValue("AutoLoginUser", accountName, RegistryValueKind.String);
        key.SetValue("AutoLoginUser_steamchina", accountName, RegistryValueKind.String);
        key.SetValue("RememberPassword", rememberPassword ? 1 : 0, RegistryValueKind.DWord);
    }

    private static T? Read<T>(RegistryKey key, string name)
    {
        var value = key.GetValue(name);
        if (value == null) return default;
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(value, target);
    }
}
