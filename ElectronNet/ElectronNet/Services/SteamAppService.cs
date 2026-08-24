using System.Collections.Concurrent;
using System.Text.Json;
using ElectronNet.Constants;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;

namespace ElectronNet.Services;

public static class SteamAppService
{
    // 共享 HttpClient，避免端口耗尽，见 HttpClientProvider
    private static HttpClient _httpClient => Helpers.HttpClientProvider.SteamApi;

    // 正在请求中的 AppID，避免重复请求
    private static readonly ConcurrentDictionary<uint, Task<string?>> _inflightFetches = new();

    /// <summary>
    /// 启动时初始化数据库
    /// </summary>
    public static async Task InitDb()
    {
        await using var db = AppDbContext.Create();

        var steamApps = db.SteamAppTable.ToList();

        // 设置所有的应用的 IsRunning 为 false（预防 Steam Stat 被强制关闭导致应用运行状态不正确）
        foreach (var steamApp in steamApps)
        {
            steamApp.IsRunning = false;
        }

        await db.SaveChangesAsync();
        await SyncDb();
    }

    /// <summary>
    /// 同步最新的数据到数据库
    /// </summary>
    public static async Task SyncDb(bool log = true)
    {
        try
        {
            var steamPath = LocalRegService.ReadSteamReg().SteamPath;
            var libraryFolderPathList = LocalFileService.ReadLibraryFoldersVdf(steamPath).Select(l => l.Path).ToList();
            var appManifestDict = LocalFileService.ReadAllAppManifestAcfs(libraryFolderPathList);
            var appManifests = appManifestDict.Values.ToList();

            await using var db = AppDbContext.Create();

            if (appManifestDict.Count == 0)
            {
                Console.WriteLine($"{ConsoleLogPrefix.DB} 没有找到应用数据");
                return;
            }

            // 查询数据库中已存在的 AppId
            var appIds = appManifestDict.Keys.ToHashSet();
            var existingApps = db.SteamAppTable.ToList();
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

            await db.SaveChangesAsync();

            // 更新 SteamApp 表的刷新时间
            await GlobalStatusService.UpdateSteamAppRefreshTime();

            if (log)
            {
                Console.WriteLine($"{ConsoleLogPrefix.DB} 成功同步 {insertCount + updateCount + deleteCount} 个应用（新增：{insertCount}，更新：{updateCount}，卸载：{deleteCount}）");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(SyncDb)} SteamApp 表失败: {ex}");
        }
    }

    /// <summary>
    /// 根据参数获取数据（支持排序和筛选）
    /// </summary>
    public static List<SteamApp> GetAllWithQuery(object? param)
    {
        try
        {
            var pd = param as Dictionary<string, object>;

            var sortField = (string?)pd?.GetValueOrDefault("sortField");
            var sortOrder = (string?)pd?.GetValueOrDefault("sortOrder");
            var filterInstalled = (bool?)pd?.GetValueOrDefault("filterInstalled");

            using var db = AppDbContext.Create();
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
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(GetAllWithQuery)} SteamApp 表失败: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// 获取所有已本地安装的应用
    /// </summary>
    public static List<SteamApp> GetAllInstalled()
    {
        try
        {
            using var db = AppDbContext.Create();
            var result = db.SteamAppTable.AsNoTracking().Where(a => a.Installed).ToList();
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(GetAllInstalled)} SteamApp 表失败: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// 获取所有本地正在运行的应用
    /// </summary>
    public static List<SteamApp> GetAllRunning()
    {
        try
        {
            using var db = AppDbContext.Create();
            var result = db.SteamAppTable.AsNoTracking().Where(a => a.IsRunning).ToList();
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(GetAllRunning)} SteamApp 表失败: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// 同步全局状态并返回全部数据（支持排序和筛选）
    /// </summary>
    public static async Task<List<SteamApp>> SyncAndGetAllWithQuery(object? param)
    {
        await SyncDb();
        return GetAllWithQuery(param);
    }

    /// <summary>
    /// 根据 AppID 获取应用名称（仅查询本地数据库）
    /// </summary>
    public static string? GetAppNameByAppId(uint appId)
    {
        try
        {
            using var db = AppDbContext.Create();
            var app = db.SteamAppTable.FirstOrDefault(a => a.AppId == (int)appId);
            return app?.Name;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.DB} GetAppNameByAppId failed for AppID {appId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 根据 AppID 获取应用名称（本地缓存优先，缺失时通过 Steam Store API 获取并缓存）
    /// </summary>
    public static async Task<string?> GetAppNameByAppIdAsync(uint appId)
    {
        if (appId == 0) return null;

        // 1. 先查本地数据库
        var localName = GetAppNameByAppId(appId);
        if (!string.IsNullOrEmpty(localName)) return localName;

        // 2. 避免同一 AppID 并发重复请求
        var task = _inflightFetches.GetOrAdd(appId, FetchAppInfoFromStoreAsync);
        try
        {
            return await task;
        }
        finally
        {
            _inflightFetches.TryRemove(appId, out _);
        }
    }

    /// <summary>
    /// 从 Steam Store API 获取应用信息并写入本地缓存
    /// </summary>
    private static async Task<string?> FetchAppInfoFromStoreAsync(uint appId)
    {
        try
        {
            var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic";
            using var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (!doc.RootElement.TryGetProperty(appId.ToString(), out var appElement)) return null;
            if (!appElement.TryGetProperty("success", out var successElement) || !successElement.GetBoolean()) return null;
            if (!appElement.TryGetProperty("data", out var dataElement)) return null;

            var name = dataElement.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
            var type = dataElement.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
            var isFree = dataElement.TryGetProperty("is_free", out var isFreeEl) && isFreeEl.GetBoolean();

            if (string.IsNullOrEmpty(name)) return null;

            // 缓存到本地数据库（标记为未安装）
            await UpsertAppCache(appId, name, type, isFree);

            Console.WriteLine($"{ConsoleLogPrefix.STEAM_APP} Fetched app name from Store API: AppID={appId} Name={name}");
            return name;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_APP} FetchAppInfoFromStoreAsync failed for AppID {appId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 将从 Store API 获取的应用信息写入本地缓存（用于未本地安装的应用）
    /// </summary>
    private static async Task UpsertAppCache(uint appId, string name, string? type, bool isFree)
    {
        try
        {
            await using var db = AppDbContext.Create();
            var existing = db.SteamAppTable.FirstOrDefault(a => a.AppId == (int)appId);
            if (existing == null)
            {
                db.SteamAppTable.Add(new SteamApp
                {
                    AppId = (int)appId,
                    Name = name,
                    NameLocalizedJson = "{}",
                    Installed = false,
                    Type = type,
                    IsFreeApp = isFree,
                    IsRunning = false
                });
            }
            else
            {
                // 只更新缺失字段，不覆盖已有数据
                if (string.IsNullOrEmpty(existing.Name)) existing.Name = name;
                if (string.IsNullOrEmpty(existing.Type)) existing.Type = type;
                existing.IsFreeApp ??= isFree;
            }

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.DB} UpsertAppCache failed for AppID {appId}: {ex.Message}");
        }
    }

    /// <summary>
    /// 批量确保 App 信息存在于本地缓存（对 Owned Games 列表使用，避免阻塞）
    /// </summary>
    public static async Task EnsureAppsCachedAsync(IEnumerable<(uint AppId, string? Name)> apps)
    {
        try
        {
            await using var db = AppDbContext.Create();
            var appList = apps.ToList();
            var appIds = appList.Select(a => (int)a.AppId).ToList();
            var existingIds = db.SteamAppTable
                .AsNoTracking()
                .Where(a => appIds.Contains(a.AppId))
                .Select(a => a.AppId)
                .ToHashSet();

            foreach (var (appId, name) in appList)
            {
                if (string.IsNullOrEmpty(name)) continue;
                if (existingIds.Contains((int)appId)) continue;

                db.SteamAppTable.Add(new SteamApp
                {
                    AppId = (int)appId,
                    Name = name,
                    NameLocalizedJson = "{}",
                    Installed = false,
                    IsRunning = false
                });
            }

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.DB} EnsureAppsCachedAsync failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 更新应用运行状态
    /// </summary>
    public static async Task UpdateAppRunningStatus(List<int> appIds, bool isRunning)
    {
        try
        {
            if (appIds.Count == 0) return;

            await using var db = AppDbContext.Create();

            // 将所有应用的 IsRunning 设置为 isRunning
            var steamApps = db.SteamAppTable
                .Where(a => appIds.Contains(a.AppId))
                .ToList();
            foreach (var steamApp in steamApps)
            {
                steamApp.IsRunning = isRunning;
            }

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(UpdateAppRunningStatus)} SteamApp 表失败: {ex.Message}");
        }
    }
}
