using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Helpers;
using SteamStat.Core.Platform;

namespace ElectronNet.Services;

public sealed class GlobalStatusService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ISteamInstallLocator installLocator,
    LocalFileService localFileService,
    TimeProvider timeProvider,
    ILogger<GlobalStatusService> logger)
{
    /// <summary>
    /// 同步最新的数据到数据库
    /// </summary>
    public async Task SyncDb(bool log = true, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var currentTime = (int)timeProvider.GetUtcNow().ToUnixTimeSeconds();

            var steamReg = installLocator.ReadSteamRegistry();
            var steamActiveProcessReg = installLocator.ReadActiveProcess();

            var globalStatus = await db.GlobalStatusTable.FirstOrDefaultAsync(g => g.Id == 1, cancellationToken);
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

            await db.SaveChangesAsync(cancellationToken);
            if (log)
            {
                logger.LogInformation("Synchronized global Steam status");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to synchronize global Steam status");
        }
    }

    /// <summary>
    /// 获取一条数据
    /// </summary>
    public GlobalStatus? GetOne()
    {
        try
        {
            using var db = dbContextFactory.CreateDbContext();
            return db.GlobalStatusTable.AsNoTracking().FirstOrDefault(g => g.Id == 1);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to read global Steam status");
            return null;
        }
    }

    /// <summary>
    /// 同步全局状态并返回全部数据
    /// </summary>
    public async Task<GlobalStatus?> SyncAndGetOne(bool log = true, CancellationToken cancellationToken = default)
    {
        await SyncDb(log, cancellationToken);
        return GetOne();
    }

    /// <summary>
    /// 更新 Steam 用户表的刷新时间
    /// </summary>
    public async Task UpdateSteamUserRefreshTime(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            var globalStatus = await db.GlobalStatusTable.FirstOrDefaultAsync(g => g.Id == 1, cancellationToken);
            if (globalStatus != null)
            {
                globalStatus.SteamUserRefreshTime = (int)timeProvider.GetUtcNow().ToUnixTimeSeconds();
                await db.SaveChangesAsync(cancellationToken);
                logger.LogDebug("Updated the Steam user refresh time");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to update the Steam user refresh time");
        }
    }

    /// <summary>
    /// 更新 Steam 应用表的刷新时间
    /// </summary>
    public async Task UpdateSteamAppRefreshTime(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            var globalStatus = await db.GlobalStatusTable.FirstOrDefaultAsync(g => g.Id == 1, cancellationToken);
            if (globalStatus != null)
            {
                globalStatus.SteamAppRefreshTime = (int)timeProvider.GetUtcNow().ToUnixTimeSeconds();
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to update the Steam app refresh time");
        }
    }

    /// <summary>
    /// 获取 Steam 库文件夹
    /// </summary>
    public List<string> GetLibraryFolders()
    {
        try
        {
            var steamPath = installLocator.ReadSteamRegistry().SteamPath;
            return localFileService.ReadLibraryFoldersVdf(steamPath)
                .Select(l => l.Path)
                .ToList();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to read Steam library folders");
            return [];
        }
    }
}
