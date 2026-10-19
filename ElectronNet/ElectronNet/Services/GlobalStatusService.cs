using ElectronNet.Models;
using Microsoft.Data.Sqlite;
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

            await SaveChangesWithBusyRetryAsync(db, cancellationToken);
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
    /// 保存更改；当其他连接（含外部进程）占用数据库写锁导致
    /// SQLITE_BUSY 并超出连接 busy_timeout 时进行有限次重试。
    /// </summary>
    private async Task SaveChangesWithBusyRetryAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        const int maxAttempts = 4;
        for (var attempt = 1;; attempt++)
        {
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException exception)
                when (attempt < maxAttempts
                      && exception.InnerException is SqliteException { SqliteErrorCode: 5 })
            {
                var delay = TimeSpan.FromMilliseconds(250 * attempt);
                logger.LogDebug(
                    "SQLite database is busy; retrying the save in {DelayMilliseconds}ms (attempt {Attempt}/{MaxAttempts})",
                    delay.TotalMilliseconds,
                    attempt,
                    maxAttempts);
                await Task.Delay(delay, timeProvider, cancellationToken);
            }
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
                await SaveChangesWithBusyRetryAsync(db, cancellationToken);
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
                await SaveChangesWithBusyRetryAsync(db, cancellationToken);
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
