using System.Text;
using System.Text.Json;
using ElectronNet.Constants;
using ElectronNet.Helpers;
using ElectronNet.Models.Dtos;
using Microsoft.Win32;
using SteamKit2;

namespace ElectronNet.Services;

public static class SteamService
{
    /// <summary>
    /// Steam 相关进程名称
    /// </summary>
    private static readonly string[] steamProcessNames = new[]
    {
#if MACOS
        "steam_osx",
#else
        "steam",
#endif
        "steamservice",
        "steamwebhelper",
        "GameOverlayUI",
    };

    /// <summary>
    /// 切换登录的用户
    /// </summary>
    public static async Task<bool> ChangeSteamUser(object? param)
    {
        try
        {
            var pd = param as Dictionary<string, object>;
            if (pd == null) return false;

            // 转换为 JSON，再反序列化为 SteamUser 对象
            var json = JsonSerializer.Serialize(pd);
            var dto = JsonSerializer.Deserialize<ChangeSteamUserDto>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (dto == null) return false;

            // 先停止 Steam Client Service（SYSTEM 权限的服务进程）
            var serviceStopSuccess = LocalProcessService.StopWindowsService("Steam Client Service");
            if (!serviceStopSuccess)
            {
                Console.WriteLine($"{ConsoleLogPrefix.WARN} Steam Client Service stop failed, but continuing with process termination");
            }

            // 获取 Steam 相关进程列表
            var steamProcesses = LocalProcessService.GetProcessesByNames(steamProcessNames);

            // 依次多线程杀死进程并等待所有进程任务完成
            var tasks = steamProcesses.Select(p => Task.Run(() => LocalProcessService.KillProcess(p))).ToList();
            await Task.WhenAll(tasks);

            // 修改注册表，设置下次登录的 Steam 用户信息
#if WINDOWS
            Registry.CurrentUser.Write(LocalRegService.STEAM_REG_PATH, "AutoLoginUser", dto.AccountName, RegistryValueKind.String);
            Registry.CurrentUser.Write(LocalRegService.STEAM_REG_PATH, "AutoLoginUser_steamchina", dto.AccountName, RegistryValueKind.String);
            Registry.CurrentUser.Write(LocalRegService.STEAM_REG_PATH, "RememberPassword", dto.RememberPassword!.Value ? 1 : 0, RegistryValueKind.DWord);
#elif LINUX || MACOS
            // TODO
#endif

            // 修改 steam_user 数据表和 loginusers.vdf 文件
            await using var db = AppDbContext.Create();
            var steamUsers = db.SteamUserTable.ToList();
            foreach (var steamUser in steamUsers)
            {
                if (steamUser.SteamId == dto.SteamId)
                {
                    steamUser.MostRecent = true;
                    steamUser.AllowAutoLogin = true;
                    steamUser.Timestamp = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                    if (dto.OfflineMode != null)
                    {
                        steamUser.WantsOfflineMode = dto.OfflineMode.Value;
                        steamUser.SkipOfflineModeWarning = true;
                    }

                    if (dto.PersonaState != null)
                    {
                        SetPersonaState(steamUser.AccountId, dto.PersonaState);
                        steamUser.WantsOfflineMode = false;
                    }
                }
                else
                {
                    steamUser.MostRecent = false;
                }
            }
            await db.SaveChangesAsync();
            LocalFileService.WriteLoginUsersVdf(LocalRegService.ReadSteamReg().SteamPath, steamUsers);

            // 关闭 Steam 询问
            SetAlwaysShowUserChooser(false);

            // 重新启动 Steam
            var newSteamProcess = LocalProcessService.StartProcess(LocalRegService.ReadSteamReg().SteamExe);
            return newSteamProcess != null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(ChangeSteamUser)} failed: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// 设置 Steam 每次启动 Steam 时是否询问使用哪个账户
    /// </summary>
    /// <param name="show">是否询问</param>
    private static void SetAlwaysShowUserChooser(bool show)
    {
        try
        {
            var configVdfPath = Path.Combine(LocalRegService.ReadSteamReg().SteamPath, "config", "config.vdf");
            
            if (string.IsNullOrWhiteSpace(configVdfPath) || !File.Exists(configVdfPath)) return;
            
            var configVdf = VdfHelper.Read(configVdfPath);
            
            var webStorage = configVdf.Children.FirstOrDefault(x => x.Name == "WebStorage");
            if (webStorage != null)
            {
                var auth = webStorage.Children.FirstOrDefault(x => x.Name == "Auth");
                if (auth != null)
                {
                    auth["AlwaysShowUserChooser"] = show;
                    VdfHelper.Write(configVdfPath, configVdf);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(SetAlwaysShowUserChooser)} failed: {ex.Message}");
        }
    }
    
    /// <summary>
    /// 设置 Steam 用户状态
    /// </summary>
    /// <param name="accountId">Steam 用户的 accountId</param>
    /// <param name="ePersonaState">用户状态</param>
    private static void SetPersonaState(int accountId, EPersonaState? ePersonaState)
    {
        try
        {
            if (ePersonaState == null) return;
            
            var steamPath = LocalRegService.ReadSteamReg().SteamPath;
            if (string.IsNullOrWhiteSpace(steamPath)) return;

            var localConfigPath = Path.Combine(steamPath, "userdata", accountId.ToString(), "config", "localconfig.vdf");
            if (string.IsNullOrWhiteSpace(localConfigPath) || !File.Exists(localConfigPath)) return;
            
            var localConfigText = File.ReadAllText(localConfigPath); // Read relevant localconfig.vdf

            // Find index of range needing to be changed.
            var positionOfVar = localConfigText.IndexOf("ePersonaState", StringComparison.Ordinal); // Find where the variable is being set
            if (positionOfVar == -1) return;
            var indexOfBefore = localConfigText.IndexOf(":", positionOfVar, StringComparison.Ordinal) + 1; // Find where the start of the variable's value is
            var indexOfAfter = localConfigText.IndexOf(",", positionOfVar, StringComparison.Ordinal); // Find where the end of the variable's value is

            // The variable is now in-between the above numbers. Remove it and insert something different here.
            var sb = new StringBuilder(localConfigText);
            _ = sb.Remove(indexOfBefore, indexOfAfter - indexOfBefore);
            _ = sb.Insert(indexOfBefore, (int)ePersonaState);
            localConfigText = sb.ToString();

            // Output
            File.WriteAllText(localConfigPath, localConfigText);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(SetPersonaState)} failed: {ex.Message}");
        }
    }
}
