using ElectronNet.Constants;
using ElectronNet.Models.LocalFiles;
using ValveKeyValue;

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
            if (string.IsNullOrWhiteSpace(steamPath)) return loginUsers;

            var loginUsersVdfPath = Path.Combine(steamPath, "config", "loginusers.vdf");
            if (!File.Exists(loginUsersVdfPath)) return loginUsers;

            var kvSerializer = KVSerializer.Create(KVSerializationFormat.KeyValues1Text);
            var vdf = kvSerializer.Deserialize(
                new FileStream(loginUsersVdfPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
                new KVSerializerOptions { HasEscapeSequences = true }
            );

            foreach (var item in vdf)
            {
                var user = new LoginUsersVdf()
                {
                    SteamID = Convert.ToInt64(item.Name),
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

            var kvSerializer = KVSerializer.Create(KVSerializationFormat.KeyValues1Text);
            var vdf = kvSerializer.Deserialize(
                new FileStream(libraryFoldersVdfPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
                new KVSerializerOptions { HasEscapeSequences = true }
            );

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

        if (string.IsNullOrWhiteSpace(appManifestAcfPath) || !File.Exists(appManifestAcfPath)) return appManifest;

        var kvSerializer = KVSerializer.Create(KVSerializationFormat.KeyValues1Text);
        var item = kvSerializer.Deserialize(
            new FileStream(appManifestAcfPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
            new KVSerializerOptions { HasEscapeSequences = true }
        );

        var installedDepots = item.Children
            .FirstOrDefault(p => p.Name == "InstalledDepots")
            ?.Children
            .ToDictionary(
                depot => Convert.ToInt32(depot.Name),
                depot => new AppManifestAcf.InstalledDepot()
                {
                    Manifest = (ulong)depot["manifest"],
                    Size = (long)depot["size"],
                    DlcAppId = ToInt32(depot["dlcappid"])
                }
            ) ?? new Dictionary<int, AppManifestAcf.InstalledDepot>();

        var sharedDepots = item.Children
            .FirstOrDefault(p => p.Name == "SharedDepots")
            ?.Children
            .ToDictionary(
                depot => Convert.ToInt32(depot.Name),
                depot => Convert.ToInt32(depot.Value)
            ) ?? new Dictionary<int, int>();

        var installScripts = item.Children
            .FirstOrDefault(p => p.Name == "InstallScripts")
            ?.Children
            .ToDictionary(
                depot => Convert.ToInt32(depot.Name),
                depot => (string)depot.Value
            ) ?? new Dictionary<int, string>();

        var userConfigObj = item.Children.FirstOrDefault(p => p.Name == "UserConfig");
        var userConfig = new AppManifestAcf.Config
        {
            Language = (string)userConfigObj?["language"],
            DisabledDlc = (string)userConfigObj?["DisabledDLC"],
            OptionalDlc = (string)userConfigObj?["optionaldlc"],
            BetaKey = (string)userConfigObj?["BetaKey"]
        };

        var mountedConfigObj = item.Children.FirstOrDefault(p => p.Name == "MountedConfig");
        var mountedConfig = new AppManifestAcf.Config
        {
            Language = (string)mountedConfigObj?["language"],
            DisabledDlc = (string)mountedConfigObj?["DisabledDLC"],
            OptionalDlc = (string)mountedConfigObj?["optionaldlc"],
            BetaKey = (string)mountedConfigObj?["BetaKey"]
        };

        appManifest = new AppManifestAcf()
        {
            AppId = (int)item["appid"],
            Universe = (int)item["universe"],
            LauncherPath = (string)item["LauncherPath"],
            Name = (string)item["name"],
            StateFlags = (int)item["StateFlags"],
            InstallDir = (string)item["installdir"],
            LastUpdated = (int)item["LastUpdated"],
            LastPlayed = (int)item["LastPlayed"],
            SizeOnDisk = (long)item["SizeOnDisk"],
            StagingSize = (long)item["StagingSize"],
            BuildId = (int)item["buildid"],
            LastOwner = (long)item["LastOwner"],
            DownloadType = ToInt32(item["DownloadType"]),
            UpdateResult = ToInt32(item["UpdateResult"]),
            BytesToDownload = ToInt64(item["BytesToDownload"]),
            BytesDownloaded = ToInt64(item["BytesDownloaded"]),
            BytesToStage = ToInt64(item["BytesToStage"]),
            BytesStaged = ToInt64(item["BytesStaged"]),
            TargetBuildID = ToInt32(item["TargetBuildID"]),
            AutoUpdateBehavior = (int)item["AutoUpdateBehavior"],
            AllowOtherDownloadsWhileRunning = (bool)item["AllowOtherDownloadsWhileRunning"],
            ScheduledAutoUpdate = (int)item["ScheduledAutoUpdate"],
            StagingFolder = ToInt32(item["StagingFolder"]),
            InstalledDepots = installedDepots,
            SharedDepots = sharedDepots,
            InstallScripts = installScripts,
            UserConfig = userConfig,
            MountedConfig = mountedConfig
        };

        return appManifest;
    }

    /// <summary>
    /// 读取 {SteamLibraryPath}\steamapps\appmanifest_{appId}.acf 文件
    /// </summary>
    public static AppManifestAcf ReadAppManifestAcf(string steamLibraryPath, int appId)
    {
        var appManifest = new AppManifestAcf();

        try
        {
            if (string.IsNullOrWhiteSpace(steamLibraryPath)) return appManifest;

            var appManifestAcfPath = Path.Combine(steamLibraryPath, "steamapps", $"appmanifest_{appId}.acf");
            if (!File.Exists(appManifestAcfPath)) return appManifest;

            appManifest = ReadAppManifestAcf(appManifestAcfPath);
            appManifest.LibraryPath = steamLibraryPath;
            return appManifest;
        }
        catch (Exception e)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} {nameof(ReadAppManifestAcf)} Failed: {e}");
            throw;
        }
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
                        appManifestDict.Add(appManifestAcf.AppId, appManifestAcf);
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
}