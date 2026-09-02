using ElectronNet.Constants;
using ElectronNet.Helpers;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using SteamStat.Core.Helpers;
using SteamStat.Core.Platform;

namespace ElectronNet.Services;

public static class GlobalStatusService
{
    /// <summary>
    /// 同步最新的数据到数据库
    /// </summary>
    public static async Task SyncDb(IDbContextFactory<AppDbContext> dbContextFactory, ISteamInstallLocator installLocator, bool log = true)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync();
            var currentTime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var steamReg = installLocator.ReadSteamRegistry();
            var steamActiveProcessReg = installLocator.ReadActiveProcess();

            var globalStatus = db.GlobalStatusTable.FirstOrDefault(g => g.Id == 1);
            var newGlobalStatus = new GlobalStatus
            {
                Id = 1,
                SteamPath = steamReg.SteamPath,
                SteamExePath = steamReg.SteamExe,
                SteamPid = steamActiveProcessReg.Pid,
                SteamClientDllPath = steamActiveProcessReg.SteamClientDll,
                SteamClientDll64Path = steamActiveProcessReg.SteamClientDll64,
                ActiveUserSteamId = SteamIdHelper.AccountIdToSteamId(steamActiveProcessReg.ActiveUser),
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
            if (log)
            {
                Console.WriteLine($"{ConsoleLogPrefix.DB} 成功同步 GlobalStatus 表");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(SyncDb)} GlobalStatus 表失败: {ex}");
        }
    }

    /// <summary>
    /// 获取一条数据
    /// </summary>
    public static GlobalStatus? GetOne(IDbContextFactory<AppDbContext> dbContextFactory)
    {
        try
        {
            using var db = dbContextFactory.CreateDbContext();
            var result = db.GlobalStatusTable.AsNoTracking().FirstOrDefault(g => g.Id == 1);
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(GetOne)} GlobalStatus 表失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 同步全局状态并返回全部数据
    /// </summary>
    public static async Task<GlobalStatus?> SyncAndGetOne(IDbContextFactory<AppDbContext> dbContextFactory, ISteamInstallLocator installLocator, bool log = true)
    {
        await SyncDb(dbContextFactory, installLocator, log);
        return GetOne(dbContextFactory);
    }

    /// <summary>
    /// 更新 Steam 用户表的刷新时间
    /// </summary>
    public static async Task UpdateSteamUserRefreshTime(IDbContextFactory<AppDbContext> dbContextFactory)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync();

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
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(UpdateSteamUserRefreshTime)} 失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 更新 Steam 应用表的刷新时间
    /// </summary>
    public static async Task UpdateSteamAppRefreshTime(IDbContextFactory<AppDbContext> dbContextFactory)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync();

            var globalStatus = db.GlobalStatusTable.FirstOrDefault(g => g.Id == 1);
            if (globalStatus != null)
            {
                var currentTime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                globalStatus.SteamAppRefreshTime = currentTime;
                await db.SaveChangesAsync();
                // Console.WriteLine($"{ConsoleLogPrefix.DB} 成功更新 Steam 应用表的刷新时间");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(UpdateSteamAppRefreshTime)} 失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 获取 Steam 库文件夹
    /// </summary>
    public static List<string> GetLibraryFolders(ISteamInstallLocator installLocator)
    {
        try
        {
            var steamPath = installLocator.ReadSteamRegistry().SteamPath;
            return LocalFileService.ReadLibraryFoldersVdf(steamPath)
                .Select(l => l.Path)
                .ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(GetLibraryFolders)} 失败: {ex.Message}");
            return [];
        }
    }
}
