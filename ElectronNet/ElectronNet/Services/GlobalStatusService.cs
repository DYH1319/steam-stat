using ElectronNet.Constants;
using ElectronNet.Models;

namespace ElectronNet.Services;

public static class GlobalStatusService
{
    /// <summary>
    /// 同步最新的数据到数据库
    /// </summary>
    public static async Task SyncDb()
    {
        try
        {
            var db = AppDbContext.Instance;
            var currentTime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var steamReg = LocalRegService.ReadSteamReg();
            var steamActiveProcessReg = LocalRegService.ReadSteamActiveProcessReg();

            var globalStatus = db.GlobalStatusTable.FirstOrDefault(g => g.Id == 1);
            var newGlobalStatus = new GlobalStatus
            {
                Id = 1,
                SteamPath = steamReg.SteamPath,
                SteamExePath = steamReg.SteamExe,
                SteamPid = steamActiveProcessReg.Pid,
                SteamClientDllPath = steamActiveProcessReg.SteamClientDll,
                SteamClientDll64Path = steamActiveProcessReg.SteamClientDll64,
                ActiveUserSteamId = AccountIdToSteamId(steamActiveProcessReg.ActiveUser),
                RunningAppId = steamReg.RunningAppID,
                RefreshTime = currentTime,
                SteamUserRefreshTime = globalStatus != null ? globalStatus.SteamUserRefreshTime : currentTime
            };

            if (globalStatus == null)
            {
                db.GlobalStatusTable.Add(newGlobalStatus);
            }
            else
            {
                db.GlobalStatusTable.Entry(globalStatus).CurrentValues.SetValues(newGlobalStatus);
            }

            await db.SaveChangesAsync();
            Console.WriteLine($"{ConsoleLogPrefix.DB} 成功同步 GlobalStatus 表");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(SyncDb)} GlobalStatus 表失败: {ex}");
        }
    }

    /// <summary>
    /// 获取一条数据
    /// </summary>
    public static GlobalStatus? GetOne()
    {
        try
        {
            var db = AppDbContext.Instance;
            var result = db.GlobalStatusTable.FirstOrDefault(g => g.Id == 1);
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(GetOne)} GlobalStatus 表失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 更新 Steam 用户表的刷新时间
    /// </summary>
    public static async Task UpdateSteamUserRefreshTime()
    {
        try
        {
            var db = AppDbContext.Instance;

            var globalStatus = db.GlobalStatusTable.FirstOrDefault(g => g.Id == 1);
            if (globalStatus != null)
            {
                var currentTime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                globalStatus.SteamUserRefreshTime = currentTime;
                await db.SaveChangesAsync();
                Console.WriteLine($"{ConsoleLogPrefix.DB} 成功更新 Steam 用户表的刷新时间");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(UpdateSteamUserRefreshTime)} GlobalStatus 表失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 将 AccountID 转换为 SteamID
    /// </summary>
    private static long? AccountIdToSteamId(int accountId)
    {
        if (accountId == 0) return null;

        // SteamID64 = 76561197960265728 + AccountID
        const long steamIdBase = 76561197960265728L;
        return steamIdBase + accountId;
    }
}