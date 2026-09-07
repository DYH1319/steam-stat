using System.Text;
using ElectronNet.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamKit2;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Platform;

namespace ElectronNet.Services;

public sealed class SteamService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ISteamInstallLocator installLocator,
    IProcessController processController,
    TimeProvider timeProvider,
    LocalFileService localFileService,
    ILogger<SteamService> logger)
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
    public async Task<bool> ChangeSteamUser(ChangeSteamUserRequest request)
    {
        try
        {
            // 转换为 JSON，再反序列化为 SteamUser 对象

            // 先停止 Steam Client Service（SYSTEM 权限的服务进程）
            var serviceStopSuccess = processController.StopWindowsService("Steam Client Service");
            if (!serviceStopSuccess)
            {
                logger.LogWarning("Steam Client Service stop failed; continuing with process termination");
            }

            // 获取 Steam 相关进程列表
            var steamProcesses = processController.GetProcessesByNames(steamProcessNames);

            // 依次多线程杀死进程并等待所有进程任务完成
            var tasks = steamProcesses.Select(p => Task.Run(() => p.KillAndWaitForExit())).ToList();
            await Task.WhenAll(tasks);

            // 修改注册表，设置下次登录的 Steam 用户信息
            installLocator.SetAutoLoginUser(request.AccountName, request.RememberPassword ?? false);

            // 修改 steam_user 数据表和 loginusers.vdf 文件
            await using var db = await dbContextFactory.CreateDbContextAsync();
            var steamUsers = db.SteamUserTable.ToList();
            foreach (var steamUser in steamUsers)
            {
                if (steamUser.SteamId == request.SteamId)
                {
                    steamUser.MostRecent = true;
                    steamUser.AllowAutoLogin = true;
                    steamUser.Timestamp = (int)timeProvider.GetUtcNow().ToUnixTimeSeconds();

                    if (request.OfflineMode != null)
                    {
                        steamUser.WantsOfflineMode = request.OfflineMode.Value;
                        steamUser.SkipOfflineModeWarning = true;
                    }

                    if (request.PersonaState != null)
                    {
                        SetPersonaState(steamUser.AccountId, (EPersonaState?)request.PersonaState, installLocator);
                        steamUser.WantsOfflineMode = false;
                    }
                }
                else
                {
                    steamUser.MostRecent = false;
                }
            }
            await db.SaveChangesAsync();
            localFileService.WriteLoginUsersVdf(installLocator.ReadSteamRegistry().SteamPath, steamUsers);

            // 关闭 Steam 询问
            SetAlwaysShowUserChooser(false, installLocator);

            // 重新启动 Steam
            var newSteamProcess = processController.StartProcess(installLocator.ReadSteamRegistry().SteamExe);
            return newSteamProcess != null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to change the active Steam user");
            return false;
        }
    }

    /// <summary>
    /// 设置 Steam 每次启动 Steam 时是否询问使用哪个账户
    /// </summary>
    /// <param name="show">是否询问</param>
    private void SetAlwaysShowUserChooser(bool show, ISteamInstallLocator installLocator)
    {
        try
        {
            var configVdfPath = Path.Combine(installLocator.ReadSteamRegistry().SteamPath, "config", "config.vdf");

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
            logger.LogError(ex, "Failed to update the Steam user chooser setting");
        }
    }

    /// <summary>
    /// 设置 Steam 用户状态
    /// </summary>
    /// <param name="accountId">Steam 用户的 accountId</param>
    /// <param name="ePersonaState">用户状态</param>
    private void SetPersonaState(int accountId, EPersonaState? ePersonaState, ISteamInstallLocator installLocator)
    {
        try
        {
            if (ePersonaState == null) return;

            var steamPath = installLocator.ReadSteamRegistry().SteamPath;
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
            logger.LogError(ex, "Failed to update the Steam persona state");
        }
    }
}
