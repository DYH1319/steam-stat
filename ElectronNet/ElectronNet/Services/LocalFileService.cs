using ElectronNet.Constants;
using ElectronNet.Helpers;
using ElectronNet.Models;
using ElectronNet.Models.LocalFiles;

namespace ElectronNet.Services;

/// <summary>
/// 本地文件读写服务
/// </summary>
public static class LocalFileService
{
    /// <summary>
    /// 读取 {SteamPath}\config\loginusers.vdf 文件
    /// </summary>
    public static List<LoginUsersVdf> ReadLoginUsersVdf(string steamPath)
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
                var user = new LoginUsersVdf()
                {
                    SteamID = item.Name,
                    AccountName = (string)item["AccountName"],
                    PersonaName = (string)item["PersonaName"],
                    RememberPassword = (bool)item["RememberPassword"],
                    WantsOfflineMode = (bool)item["WantsOfflineMode"],
                    SkipOfflineModeWarning = (bool)item["SkipOfflineModeWarning"],
                    AllowAutoLogin = (bool)item["AllowAutoLogin"],
                    MostRecent = (bool)item["MostRecent"],
                    Timestamp = (int)item["Timestamp"]
                };

                loginUsers.Add(user);
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(ReadLoginUsersVdf)} Failed: {e.Message}");
            throw;
        }

        return loginUsers;
    }

    /// <summary>
    /// 写入 {SteamPath}\config\loginusers.vdf 文件
    /// </summary>
    public static bool WriteLoginUsersVdf(string steamPath, List<SteamUser> users)
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

                item["AccountName"] = user.AccountName;
                item["PersonaName"] = user.PersonaName;
                item["RememberPassword"] = user.RememberPassword;
                item["WantsOfflineMode"] = user.WantsOfflineMode;
                item["SkipOfflineModeWarning"] = user.SkipOfflineModeWarning;
                item["AllowAutoLogin"] = user.AllowAutoLogin;
                item["MostRecent"] = user.MostRecent;
                item["Timestamp"] = user.Timestamp;
            }

            VdfHelper.Write(loginUsersVdfPath, vdf);

            return true;
        }
        catch (Exception e)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(WriteLoginUsersVdf)} Failed: {e.Message}");
            throw;
        }
    }

    /// <summary>
    /// 读取 {SteamPath}\config\libraryfolders.vdf 文件
    /// </summary>
    public static List<LibraryFoldersVdf> ReadLibraryFoldersVdf(string steamPath)
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
                    Path = (string)item["path"],
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
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(ReadLibraryFoldersVdf)} Failed: {e.Message}");
            throw;
        }

        return libraryFolders;
    }

    /// <summary>
    /// 读取 {SteamLibraryPath}\steamapps\appmanifest_{appId}.acf 文件
    /// </summary>
    private static AppManifestAcf ReadAppManifestAcf(string appManifestAcfPath)
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
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(ReadAppManifestAcf)} Failed. Acf file Path: {appManifestAcfPath}; ex: {ex}");
        }

        return appManifest;
    }

    /// <summary>
    /// 读取所有的 {SteamLibraryPath}\steamapps\appmanifest_{appId}.acf 文件
    /// </summary>
    public static Dictionary<int, AppManifestAcf> ReadAllAppManifestAcfs(List<string> steamLibraryPaths)
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
                            Console.WriteLine($"{ConsoleLogPrefix.ERROR} TryAdd appManifest in syncDb failed. AppId: {appManifestAcf.AppId}. Acf file path: {appManifestAcfPath}");
                        }
                    }
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(ReadAllAppManifestAcfs)} Failed: {e}");
            throw;
        }

        return appManifestDict;
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