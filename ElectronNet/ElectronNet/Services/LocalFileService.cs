using ElectronNet.Helpers;
using ElectronNet.Models;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Helpers;
using SteamStat.Core.Models.LocalFiles;
using ValveKeyValue;

namespace ElectronNet.Services;

/// <summary>
/// 本地文件读写服务
/// </summary>
public sealed class LocalFileService(ILogger<LocalFileService> logger)
{
    /// <summary>
    /// 读取 {SteamPath}\config\loginusers.vdf 文件
    /// </summary>
    public List<LoginUsersVdf> ReadLoginUsersVdf(string steamPath)
    {
        var loginUsers = new List<LoginUsersVdf>();

        try
        {
            if (string.IsNullOrWhiteSpace(steamPath)) return [];

            var loginUsersVdfPath = Path.Combine(steamPath, "config", "loginusers.vdf");
            if (!File.Exists(loginUsersVdfPath)) return [];

            var vdf = VdfHelper.Read(loginUsersVdfPath);

            foreach (var item in vdf)
            {
                if (!ulong.TryParse(item.Name, out _) || item["AccountName"] is not { } accountName)
                {
                    logger.LogWarning("Ignored an invalid Steam login-users entry");
                    continue;
                }

                var user = new LoginUsersVdf()
                {
                    SteamID = item.Name,
                    AccountName = (string)accountName,
                    PersonaName = item["PersonaName"] is { } personaName ? (string)personaName : string.Empty,
                    RememberPassword = item["RememberPassword"] is { } rememberPassword && (bool)rememberPassword,
                    WantsOfflineMode = item["WantsOfflineMode"] is { } wantsOfflineMode && (bool)wantsOfflineMode,
                    SkipOfflineModeWarning = item["SkipOfflineModeWarning"] is { } skipOfflineModeWarning && (bool)skipOfflineModeWarning,
                    AllowAutoLogin = item["AllowAutoLogin"] is { } allowAutoLogin && (bool)allowAutoLogin,
                    MostRecent = item["MostRecent"] is { } mostRecent && (bool)mostRecent,
                    Timestamp = ReadLoginTimestamp(item)
                };

                loginUsers.Add(user);
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to read Steam login users VDF");
            throw;
        }

        return loginUsers;
    }

    /// <summary>
    /// 写入 {SteamPath}\config\loginusers.vdf 文件
    /// </summary>
    public bool WriteLoginUsersVdf(string steamPath, List<SteamUser> users)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(steamPath)) return false;

            var loginUsersVdfPath = Path.Combine(steamPath, "config", "loginusers.vdf");
            if (!File.Exists(loginUsersVdfPath)) return false;

            var vdf = VdfHelper.Read(loginUsersVdfPath);

            foreach (var item in vdf)
            {
                var user = users.FirstOrDefault(u => u.SteamId.ToString() == item.Name);
                if (user == null) continue;

                // KVValue 的隐式转换不接受 null，DB 中的可空字段一律给安全默认值
                item["AccountName"] = user.AccountName ?? string.Empty;
                item["PersonaName"] = user.PersonaName ?? string.Empty;
                item["RememberPassword"] = user.RememberPassword ?? false;
                item["WantsOfflineMode"] = user.WantsOfflineMode ?? false;
                item["SkipOfflineModeWarning"] = user.SkipOfflineModeWarning ?? false;
                item["AllowAutoLogin"] = user.AllowAutoLogin ?? false;
                item["MostRecent"] = user.MostRecent ?? false;
                WriteLoginTimestamp(item, user.Timestamp);
            }

            VdfHelper.Write(loginUsersVdfPath, vdf);

            return true;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to write Steam login users VDF");
            throw;
        }
    }

    /// <summary>
    /// 读取最后登录时间。Steam 当前将该键写为小写 "timestamp"，而历史上为大写 "Timestamp"；
    /// 两种键可能同时存在（旧版本软件回写留下的副本），因此按大小写不敏感匹配并取最大值。
    /// </summary>
    private static int ReadLoginTimestamp(KVObject item)
    {
        var timestamp = 0;
        foreach (var child in item.Children)
        {
            if (string.Equals(child.Name, "timestamp", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(child.Value?.ToString(), out var value))
            {
                timestamp = Math.Max(timestamp, value);
            }
        }
        return timestamp;
    }

    /// <summary>
    /// 写入最后登录时间。Steam 当前使用小写 "timestamp"；文件中若残留大写 "Timestamp" 副本
    /// 会一并同步为相同值，避免读到过期副本。不存在该键时按 Steam 现状补小写键。
    /// </summary>
    private static void WriteLoginTimestamp(KVObject item, int? timestamp)
    {
        if (timestamp is not { } value) return;

        var keys = item.Children
            .Where(child => string.Equals(child.Name, "timestamp", StringComparison.OrdinalIgnoreCase))
            .Select(child => child.Name)
            .ToList();
        if (keys.Count == 0)
        {
            item["timestamp"] = value;
            return;
        }
        foreach (var key in keys)
        {
            item[key] = value;
        }
    }

    /// <summary>
    /// 读取 {SteamPath}\config\libraryfolders.vdf 文件
    /// </summary>
    public List<LibraryFoldersVdf> ReadLibraryFoldersVdf(string steamPath)
    {
        var libraryFolders = new List<LibraryFoldersVdf>();

        try
        {
            if (string.IsNullOrWhiteSpace(steamPath)) return libraryFolders;

            var libraryFoldersVdfPath = Path.Combine(steamPath, "config", "libraryfolders.vdf");
            if (!File.Exists(libraryFoldersVdfPath)) return libraryFolders;

            var vdf = VdfHelper.Read(libraryFoldersVdfPath);

            foreach (var item in vdf)
            {
                var apps = item.Children
                    .FirstOrDefault(p => p.Name == "apps")
                    ?.Children
                    .ToDictionary(
                        app => Convert.ToInt32(app.Name),
                        app => Convert.ToInt64(app.Value)
                    ) ?? new Dictionary<int, long>();

                var library = new LibraryFoldersVdf()
                {
                    Index = Convert.ToInt32(item.Name),
                    Path = PathHelper.ToTrueCase((string)item["path"]),
                    Label = (string)item["label"],
                    ContentId = (long)item["contentid"],
                    TotalSize = (long)item["totalsize"],
                    UpdateCleanBytesTally = (long)item["update_clean_bytes_tally"],
                    TimeLastUpdateVerified = (int)item["time_last_update_verified"],
                    Apps = apps
                };

                libraryFolders.Add(library);
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to read Steam library folders VDF");
            throw;
        }

        return libraryFolders;
    }

    /// <summary>
    /// 读取 {SteamLibraryPath}\steamapps\appmanifest_{appId}.acf 文件
    /// </summary>
    private AppManifestAcf ReadAppManifestAcf(string appManifestAcfPath)
    {
        var appManifest = new AppManifestAcf();

        try
        {
            if (string.IsNullOrWhiteSpace(appManifestAcfPath) || !File.Exists(appManifestAcfPath)) return appManifest;

            var item = VdfHelper.Read(appManifestAcfPath);

            var installedDepots = item.Children
                .FirstOrDefault(p => p.Name == "InstalledDepots")
                ?.Children
                .ToDictionary(
                    depot => Convert.ToInt32(depot.Name),
                    depot => new AppManifestAcf.InstalledDepot()
                    {
                        Manifest = ToUInt64(depot["manifest"]),
                        Size = ToInt64(depot["size"]),
                        DlcAppId = ToInt32(depot["dlcappid"])
                    }
                );

            var sharedDepots = item.Children
                .FirstOrDefault(p => p.Name == "SharedDepots")
                ?.Children
                .ToDictionary(
                    depot => Convert.ToInt32(depot.Name),
                    depot => ToInt32(depot.Value)
                );

            var installScripts = item.Children
                .FirstOrDefault(p => p.Name == "InstallScripts")
                ?.Children
                .ToDictionary(
                    depot => Convert.ToInt32(depot.Name),
                    depot => ToString(depot.Value)
                );

            var userConfigObj = item.Children.FirstOrDefault(p => p.Name == "UserConfig");
            var userConfig = new AppManifestAcf.Config
            {
                Language = ToString(userConfigObj?["language"]),
                DisabledDlc = ToString(userConfigObj?["DisabledDLC"]),
                OptionalDlc = ToString(userConfigObj?["optionaldlc"]),
                BetaKey = ToString(userConfigObj?["BetaKey"])
            };

            var mountedConfigObj = item.Children.FirstOrDefault(p => p.Name == "MountedConfig");
            var mountedConfig = new AppManifestAcf.Config
            {
                Language = ToString(mountedConfigObj?["language"]),
                DisabledDlc = ToString(mountedConfigObj?["DisabledDLC"]),
                OptionalDlc = ToString(mountedConfigObj?["optionaldlc"]),
                BetaKey = ToString(mountedConfigObj?["BetaKey"])
            };

            appManifest = new AppManifestAcf()
            {
                AppId = Convert.ToInt32(item["appid"]),
                Universe = ToInt32(item["universe"]),
                LauncherPath = ToString(item["LauncherPath"]),
                Name = ToString(item["name"]),
                StateFlags = ToInt32(item["StateFlags"]),
                InstallDir = ToString(item["installdir"]),
                LastUpdated = ToInt32(item["LastUpdated"]),
                LastPlayed = ToInt32(item["LastPlayed"]),
                SizeOnDisk = ToInt64(item["SizeOnDisk"]),
                StagingSize = ToInt64(item["StagingSize"]),
                BuildId = ToInt32(item["buildid"]),
                LastOwner = ToInt64(item["LastOwner"]),
                DownloadType = ToInt32(item["DownloadType"]),
                UpdateResult = ToInt32(item["UpdateResult"]),
                BytesToDownload = ToInt64(item["BytesToDownload"]),
                BytesDownloaded = ToInt64(item["BytesDownloaded"]),
                BytesToStage = ToInt64(item["BytesToStage"]),
                BytesStaged = ToInt64(item["BytesStaged"]),
                TargetBuildID = ToInt32(item["TargetBuildID"]),
                AutoUpdateBehavior = ToInt32(item["AutoUpdateBehavior"]),
                AllowOtherDownloadsWhileRunning = ToBoolean(item["AllowOtherDownloadsWhileRunning"]),
                ScheduledAutoUpdate = ToInt32(item["ScheduledAutoUpdate"]),
                StagingFolder = ToInt32(item["StagingFolder"]),
                InstalledDepots = installedDepots,
                SharedDepots = sharedDepots,
                InstallScripts = installScripts,
                UserConfig = userConfig,
                MountedConfig = mountedConfig
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read Steam app manifest {ManifestPath}", appManifestAcfPath);
        }

        return appManifest;
    }

    /// <summary>
    /// 读取所有的 {SteamLibraryPath}\steamapps\appmanifest_{appId}.acf 文件
    /// </summary>
    public Dictionary<int, AppManifestAcf> ReadAllAppManifestAcfs(List<string> steamLibraryPaths)
    {
        var appManifestDict = new Dictionary<int, AppManifestAcf>();

        try
        {
            foreach (var libraryPath in steamLibraryPaths)
            {
                if (string.IsNullOrWhiteSpace(libraryPath)) continue;

                var steamAppsPath = Path.Combine(libraryPath, "steamapps");
                if (!Directory.Exists(steamAppsPath)) continue;

                foreach (var appManifestAcfPath in Directory.GetFiles(steamAppsPath, "appmanifest_*.acf"))
                {
                    var appManifestAcf = ReadAppManifestAcf(appManifestAcfPath);
                    if (appManifestAcf.AppId > 0)
                    {
                        appManifestAcf.LibraryPath = libraryPath;
                        if (!appManifestDict.TryAdd(appManifestAcf.AppId, appManifestAcf))
                        {
                            logger.LogWarning("Ignored duplicate Steam app manifest for {AppId} at {ManifestPath}", appManifestAcf.AppId, appManifestAcfPath);
                        }
                    }
                }
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to read Steam app manifests");
            throw;
        }

        return appManifestDict;
    }

    /// <summary>
    /// 扫描所有 {SteamLibraryPath}\steamapps\appmanifest_*.acf 文件，统计总数与解析失败数
    /// </summary>
    public AppManifestScanResult ScanAppManifests(IEnumerable<string> libraryPaths)
    {
        var total = 0;
        var failed = 0;
        foreach (var libraryPath in libraryPaths)
        {
            if (string.IsNullOrWhiteSpace(libraryPath)) continue;

            var steamAppsPath = Path.Combine(libraryPath, "steamapps");
            if (!Directory.Exists(steamAppsPath)) continue;

            foreach (var appManifestAcfPath in Directory.GetFiles(steamAppsPath, "appmanifest_*.acf"))
            {
                total++;
                if (ReadAppManifestAcf(appManifestAcfPath).AppId <= 0) failed++;
            }
        }
        return new AppManifestScanResult(total, failed);
    }

    /// <summary>
    /// 将值转换为 int32，若 obj 为 null，返回 null
    /// </summary>
    private static int? ToInt32(object? obj)
    {
        if (obj == null) return null;
        return Convert.ToInt32(obj);
    }

    /// <summary>
    /// 将值转换为 int64，若 obj 为 null，返回 null
    /// </summary>
    private static long? ToInt64(object? obj)
    {
        if (obj == null) return null;
        return Convert.ToInt64(obj);
    }

    /// <summary>
    /// 将值转换为 uint64，若 obj 为 null，返回 null
    /// </summary>
    private static ulong? ToUInt64(object? obj)
    {
        if (obj == null) return null;
        return Convert.ToUInt64(obj);
    }

    /// <summary>
    /// 将值转换为 bool，若 obj 为 null，返回 null
    /// </summary>
    private static bool? ToBoolean(object? obj)
    {
        if (obj == null) return null;
        return Convert.ToBoolean(obj);
    }

    /// <summary>
    /// 将值转换为 string，若 obj 为 null，返回 null
    /// </summary>
    private static string? ToString(object? obj)
    {
        if (obj == null) return null;
        return Convert.ToString(obj);
    }
}

/// <summary>appmanifest_*.acf 扫描结果：总数与解析失败数</summary>
public sealed record AppManifestScanResult(int Total, int Failed);
