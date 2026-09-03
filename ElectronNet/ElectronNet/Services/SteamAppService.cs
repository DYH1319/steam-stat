using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Platform;

namespace ElectronNet.Services;

public sealed class SteamAppService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ISteamInstallLocator installLocator,
    LocalFileService localFileService,
    GlobalStatusService globalStatusService,
    ILogger<SteamAppService> logger)
{
    /// <summary>
    /// 启动时初始化数据库
    /// </summary>
    public async Task InitDb(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var steamApps = await db.SteamAppTable.ToListAsync(cancellationToken);

        // 设置所有的应用的 IsRunning 为 false（预防 Steam Stat 被强制关闭导致应用运行状态不正确）
        foreach (var steamApp in steamApps)
        {
            steamApp.IsRunning = false;
        }

        await db.SaveChangesAsync(cancellationToken);
        await SyncDb(cancellationToken: cancellationToken);
    }

    /// <summary>
    /// 同步最新的数据到数据库
    /// </summary>
    public async Task SyncDb(bool log = true, CancellationToken cancellationToken = default)
    {
        try
        {
            var steamPath = installLocator.ReadSteamRegistry().SteamPath;
            var libraryFolderPathList = localFileService.ReadLibraryFoldersVdf(steamPath).Select(l => l.Path).ToList();
            var appManifestDict = localFileService.ReadAllAppManifestAcfs(libraryFolderPathList);
            var appManifests = appManifestDict.Values.ToList();

            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            if (appManifestDict.Count == 0)
            {
                logger.LogInformation("No installed Steam app data was found");
                return;
            }

            // 查询数据库中已存在的 AppId
            var appIds = appManifestDict.Keys.ToHashSet();
            var existingApps = await db.SteamAppTable.ToListAsync(cancellationToken);
            var existingAppIds = existingApps.Select(u => u.AppId).ToHashSet();

            // 分离新增/更新/删除的应用
            var appsToInsert = appManifests.Where(a => !existingAppIds.Contains(a.AppId)).ToList(); // 文件中存在，数据库中不存在
            var appsToUpdate = appManifests.Where(a => existingAppIds.Contains(a.AppId)).ToList(); // 文件中存在，数据中存在
            var appsToDelete = existingApps.Where(a => !appIds.Contains(a.AppId)).ToList(); // 数据库中存在，文件中不存在

            int insertCount = 0;
            int updateCount = 0;
            int deleteCount = 0;

            // 插入新应用
            foreach (var appAcf in appsToInsert)
            {
                var newApp = new SteamApp
                {
                    AppId = appAcf.AppId,
                    Name = appAcf.Name,
                    NameLocalizedJson = "{}",
                    Installed = true,
                    InstallDir = appAcf.InstallDir,
                    InstallDirPath = appAcf.LibraryPath + @"\steamapps\common\" + appAcf.InstallDir,
                    AppOnDisk = appAcf.SizeOnDisk,
                    AppOnDiskReal = null,
                    IsRunning = false,
                    Type = null,
                    Developer = null,
                    Publisher = null,
                    SteamReleaseDate = null,
                    IsFreeApp = null
                };

                db.SteamAppTable.Add(newApp);
                insertCount++;
            }

            // 更新已存在的应用
            foreach (var appAcf in appsToUpdate)
            {
                var existingApp = existingApps.First(a => a.AppId == appAcf.AppId);

                existingApp.AppId = appAcf.AppId;
                existingApp.Name = appAcf.Name;
                existingApp.NameLocalizedJson = "{}";
                existingApp.Installed = true;
                existingApp.InstallDir = appAcf.InstallDir;
                existingApp.InstallDirPath = appAcf.LibraryPath + @"\steamapps\common\" + appAcf.InstallDir;
                existingApp.AppOnDisk = appAcf.SizeOnDisk;
                existingApp.AppOnDiskReal = null;
                existingApp.IsRunning = existingApp.IsRunning;
                existingApp.Type = null;
                existingApp.Developer = null;
                existingApp.Publisher = null;
                existingApp.SteamReleaseDate = null;
                existingApp.IsFreeApp = null;

                updateCount++;
            }

            // 卸载不存在的应用，设置 Installed 为 false
            foreach (var steamApp in appsToDelete)
            {
                steamApp.Installed = false;
                steamApp.AppOnDisk = 0L;
                steamApp.IsRunning = false;

                deleteCount++;
            }

            await db.SaveChangesAsync(cancellationToken);

            // 更新 SteamApp 表的刷新时间
            await globalStatusService.UpdateSteamAppRefreshTime(cancellationToken);

            if (log)
            {
                logger.LogInformation("Synchronized {AppCount} Steam apps: {InsertedCount} inserted, {UpdatedCount} updated, {RemovedCount} removed", insertCount + updateCount + deleteCount, insertCount, updateCount, deleteCount);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to synchronize Steam apps");
        }
    }

    /// <summary>
    /// 根据参数获取数据（支持排序和筛选）
    /// </summary>
    public List<SteamApp> GetAllWithQuery(SteamAppsQueryRequest param)
    {
        try
        {
            var sortField = param.SortField;
            var sortOrder = param.SortOrder;
            var filterInstalled = param.FilterInstalled;

            using var db = dbContextFactory.CreateDbContext();
            var query = db.SteamAppTable.AsNoTracking();

            // 筛选
            query = query.Where(a => filterInstalled == null || a.Installed == filterInstalled);

            // 排序
            var isDesc = sortOrder == "desc";
            query = sortField switch
            {
                "appId" => isDesc ? query.OrderByDescending(a => a.AppId) : query.OrderBy(a => a.AppId),
                "name" => isDesc ? query.OrderByDescending(a => a.Name) : query.OrderBy(a => a.Name),
                "installDir" => isDesc ? query.OrderByDescending(a => a.InstallDir) : query.OrderBy(a => a.InstallDir),
                "appOnDisk" => isDesc ? query.OrderByDescending(a => a.AppOnDisk) : query.OrderBy(a => a.AppOnDisk),
                _ => query
            };

            return query.ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to query Steam apps");
            return [];
        }
    }

    /// <summary>
    /// 获取所有已本地安装的应用
    /// </summary>
    public List<SteamApp> GetAllInstalled()
    {
        try
        {
            using var db = dbContextFactory.CreateDbContext();
            var result = db.SteamAppTable.AsNoTracking().Where(a => a.Installed).ToList();
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to query installed Steam apps");
            return [];
        }
    }

    /// <summary>
    /// 获取所有本地正在运行的应用
    /// </summary>
    public List<SteamApp> GetAllRunning()
    {
        try
        {
            using var db = dbContextFactory.CreateDbContext();
            var result = db.SteamAppTable.AsNoTracking().Where(a => a.IsRunning).ToList();
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to query running Steam apps");
            return [];
        }
    }

    /// <summary>
    /// 同步全局状态并返回全部数据（支持排序和筛选）
    /// </summary>
    public async Task<List<SteamApp>> SyncAndGetAllWithQuery(SteamAppsQueryRequest param, CancellationToken cancellationToken = default)
    {
        await SyncDb(cancellationToken: cancellationToken);
        return GetAllWithQuery(param);
    }

    /// <summary>
    /// 更新应用运行状态
    /// </summary>
    public async Task UpdateAppRunningStatus(List<int> appIds, bool isRunning, CancellationToken cancellationToken = default)
    {
        try
        {
            if (appIds.Count == 0) return;

            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            // 将所有应用的 IsRunning 设置为 isRunning
            var steamApps = await db.SteamAppTable
                .Where(a => appIds.Contains(a.AppId))
                .ToListAsync(cancellationToken);
            foreach (var steamApp in steamApps)
            {
                steamApp.IsRunning = isRunning;
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update running state for {AppCount} Steam apps", appIds.Count);
        }
    }
}
