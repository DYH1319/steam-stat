using ElectronNet.Constants;
using ElectronNet.Models.LocalRegs;
using Microsoft.Win32;

namespace ElectronNet.Services;

/// <summary>
/// 本地注册表读写服务
/// </summary>
public static class LocalRegService
{
    private const string STEAM_REG_PATH = @"Software\Valve\Steam";
    private const string STEAM_ACTIVE_PROCESS_REG_PATH = @"Software\Valve\Steam\ActiveProcess";
    private const string STEAM_APP_REG_PATH = @"Software\Valve\Steam\Apps";

    /// <summary>
    /// 读取 HKEY_CURRENT_USER\Software\Valve\Steam 注册表
    /// </summary>
    public static SteamReg ReadSteamReg()
    {
        var steamReg = new SteamReg();
        using var registryKey = Registry.CurrentUser.OpenSubKey(STEAM_REG_PATH);

        try
        {
            if (registryKey == null) return steamReg;

            steamReg.AlreadyRetriedOfflineMode = registryKey.Read<int>("AlreadyRetriedOfflineMode");
            steamReg.AutoLoginUser = registryKey.Read<string>("AutoLoginUser") ?? string.Empty;
            steamReg.AutoLoginUserSteamChina = registryKey.Read<string>("AutoLoginUser_steamchina") ?? string.Empty;
            steamReg.CompletedOOBEStage1 = registryKey.Read<int>("CompletedOOBEStage1");
            steamReg.Language = registryKey.Read<string>("Language") ?? string.Empty;
            steamReg.LastGameNameUsed = registryKey.Read<string>("LastGameNameUsed") ?? string.Empty;
            steamReg.PseudoUUID = registryKey.Read<string>("PseudoUUID") ?? string.Empty;
            steamReg.Rate = registryKey.Read<string>("Rate") ?? string.Empty;
            steamReg.RememberPassword = registryKey.Read<int>("RememberPassword");
            steamReg.Restart = registryKey.Read<int>("Restart");
            steamReg.RunningAppID = registryKey.Read<int>("RunningAppID");
            steamReg.Skin = registryKey.Read<string>("Skin") ?? string.Empty;
            steamReg.SourceModInstallPath = registryKey.Read<string>("SourceModInstallPath") ?? string.Empty;
            steamReg.StartupModeTmp = registryKey.Read<int>("StartupModeTmp");
            steamReg.StartupModeTmpIsValid = registryKey.Read<int>("StartupModeTmpIsValid");
            steamReg.SteamExe = registryKey.Read<string>("SteamExe") ?? string.Empty;
            steamReg.SteamPath = registryKey.Read<string>("SteamPath") ?? string.Empty;
            steamReg.SuppressAutoRun = registryKey.Read<int>("SuppressAutoRun");
        }
        catch (Exception e)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(ReadSteamReg)} Failed: {e.Message}");
        }
        finally
        {
            registryKey?.Close();
        }

        return steamReg;
    }

    /// <summary>
    /// 读取 HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess 注册表
    /// </summary>
    public static SteamActiveProcessReg ReadSteamActiveProcessReg()
    {
        var steamActiveProcessReg = new SteamActiveProcessReg();
        using var registryKey = Registry.CurrentUser.OpenSubKey(STEAM_ACTIVE_PROCESS_REG_PATH);

        try
        {
            if (registryKey == null) return steamActiveProcessReg;

            steamActiveProcessReg.ActiveUser = registryKey.Read<int>("ActiveUser");
            steamActiveProcessReg.Pid = registryKey.Read<int>("pid");
            steamActiveProcessReg.SteamClientDll = registryKey.Read<string>("SteamClientDll") ?? string.Empty;
            steamActiveProcessReg.SteamClientDll64 = registryKey.Read<string>("SteamClientDll64") ?? string.Empty;
            steamActiveProcessReg.Universe = registryKey.Read<string>("Universe") ?? string.Empty;
        }
        catch (Exception e)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(ReadSteamActiveProcessReg)} Failed: {e.Message}");
        }
        finally
        {
            registryKey?.Close();
        }

        return steamActiveProcessReg;
    }

    /// <summary>
    /// 读取 HKEY_CURRENT_USER\Software\Valve\Steam\Apps\{appId} 注册表
    /// </summary>
    public static Dictionary<int, SteamAppReg> ReadSteamAppRegs()
    {
        var steamAppRegDict = new Dictionary<int, SteamAppReg>();
        using var rootKey = Registry.CurrentUser.OpenSubKey(STEAM_APP_REG_PATH);

        try
        {
            if (rootKey == null) return steamAppRegDict;

            foreach (var subKeyName in rootKey.GetSubKeyNames())
            {
                using var subKey = rootKey.OpenSubKey(subKeyName);
                if (subKey == null) continue;

                var steamAppReg = new SteamAppReg
                {
                    AppId = Convert.ToInt32(subKeyName),
                    Firewall = subKey.Read<int?>("firewall"),
                    FmSysInfo = subKey.Read<int?>("FmSysInfo"),
                    Cloud = subKey.Read<int?>("Cloud"),
                    Installed = subKey.Read<int?>("Installed"),
                    Name = subKey.Read<string?>("Name"),
                    Running = subKey.Read<int?>("Running"),
                    Updating = subKey.Read<int?>("Updating"),
                };

                steamAppRegDict.Add(Convert.ToInt32(subKeyName), steamAppReg);
                subKey.Close();
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(ReadSteamAppRegs)} Failed: {e.Message}");
        }
        finally
        {
            rootKey?.Close();
        }

        return steamAppRegDict;
    }
}
