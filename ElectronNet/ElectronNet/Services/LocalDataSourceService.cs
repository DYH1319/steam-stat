using ElectronNet.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Platform;
using SteamStat.Core.Settings;

namespace ElectronNet.Services;

/// <summary>
/// 汇总本地数据源（注册表 / Steam 配置文件 / 后台任务）的可用性状态
/// </summary>
public sealed class LocalDataSourceService(
    ISteamInstallLocator installLocator,
    LocalFileService localFileService,
    IDbContextFactory<AppDbContext> dbContextFactory,
    ISettingsStore settingsStore,
    TimeProvider timeProvider,
    ILogger<LocalDataSourceService> logger)
{
    private const string SteamRegistryPath = @"HKCU\Software\Valve\Steam";

    public async Task<SteamLocalDataSourcesDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var steamPath = GetSteamPath();

        var libraryFolders = CheckFileEntry(
            "file.libraryFoldersVdf",
            steamPath == null ? null : Path.Combine(steamPath, "config", "libraryfolders.vdf"),
            path => localFileService.ReadLibraryFoldersVdf(steamPath!).Count);

        var libraryPaths = libraryFolders.Status == "ok" && steamPath != null
            ? ReadLibraryPaths(steamPath)
            : null;

        var sources = new List<SteamLocalDataSourceDto>
        {
            CheckSteamRegistryKey(steamPath),
            CheckRegistryKey("registry.activeProcess", SteamRegistryKey.ActiveProcess, $@"{SteamRegistryPath}\ActiveProcess"),
            CheckRegistryApps(),
            CheckFileEntry(
                "file.loginUsersVdf",
                steamPath == null ? null : Path.Combine(steamPath, "config", "loginusers.vdf"),
                _ => localFileService.ReadLoginUsersVdf(steamPath!).Count),
            libraryFolders,
            CheckAppManifests(libraryPaths),
            CheckFileEntry(
                "file.configVdf",
                steamPath == null ? null : Path.Combine(steamPath, "config", "config.vdf"),
                path => { VdfHelper.Read(path); return null; }),
            CheckLocalConfigVdf(steamPath),
            await CheckAppRunningStatusJobAsync(cancellationToken).ConfigureAwait(false)
        };

        return new SteamLocalDataSourcesDto
        {
            Sources = sources,
            CheckedAt = timeProvider.GetUtcNow().ToUnixTimeSeconds()
        };
    }

    private string? GetSteamPath()
    {
        try
        {
            var steamPath = installLocator.ReadSteamRegistry().SteamPath;
            return string.IsNullOrWhiteSpace(steamPath) ? null : steamPath;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Failed to resolve the Steam installation path");
            return null;
        }
    }

    private List<string>? ReadLibraryPaths(string steamPath)
    {
        try
        {
            return localFileService.ReadLibraryFoldersVdf(steamPath).Select(folder => folder.Path).ToList();
        }
        catch
        {
            return null;
        }
    }

    private SteamLocalDataSourceDto CheckSteamRegistryKey(string? steamPath)
    {
        const string id = "registry.steam";
        try
        {
            if (!installLocator.HasRegistryKey(SteamRegistryKey.Steam))
                return Source(id, "registry", SteamRegistryPath, "missing");
            if (steamPath == null)
                return Source(id, "registry", SteamRegistryPath, "parseFailed", error: "SteamPath is empty");
            return Source(id, "registry", SteamRegistryPath, "ok");
        }
        catch (Exception exception)
        {
            return Source(id, "registry", SteamRegistryPath, "parseFailed", error: exception.Message);
        }
    }

    private SteamLocalDataSourceDto CheckRegistryKey(string id, SteamRegistryKey key, string displayPath)
    {
        try
        {
            return Source(id, "registry", displayPath, installLocator.HasRegistryKey(key) ? "ok" : "missing");
        }
        catch (Exception exception)
        {
            return Source(id, "registry", displayPath, "parseFailed", error: exception.Message);
        }
    }

    private SteamLocalDataSourceDto CheckRegistryApps()
    {
        const string id = "registry.apps";
        var displayPath = $@"{SteamRegistryPath}\Apps";
        try
        {
            if (!installLocator.HasRegistryKey(SteamRegistryKey.Apps))
                return Source(id, "registry", displayPath, "missing");
            return Source(id, "registry", displayPath, "ok", itemCount: installLocator.ReadAppRegistry().Count);
        }
        catch (Exception exception)
        {
            return Source(id, "registry", displayPath, "parseFailed", error: exception.Message);
        }
    }

    private SteamLocalDataSourceDto CheckFileEntry(string id, string? path, Func<string, int?> read)
    {
        try
        {
            if (path == null || !File.Exists(path))
                return Source(id, "file", path, "missing");
            return Source(id, "file", path, "ok", itemCount: read(path));
        }
        catch (Exception exception)
        {
            return Source(id, "file", path, "parseFailed", error: exception.Message);
        }
    }

    private SteamLocalDataSourceDto CheckAppManifests(List<string>? libraryPaths)
    {
        const string id = "file.appManifests";
        var displayPath = libraryPaths == null
            ? null
            : string.Join("; ", libraryPaths.Select(library => Path.Combine(library, "steamapps", "appmanifest_*.acf")));
        try
        {
            if (libraryPaths == null)
                return Source(id, "file", displayPath, "missing");
            var scan = localFileService.ScanAppManifests(libraryPaths);
            if (scan.Total == 0)
                return Source(id, "file", displayPath, "missing");
            return scan.Failed > 0
                ? Source(id, "file", displayPath, "parseFailed", itemCount: scan.Total, failedCount: scan.Failed)
                : Source(id, "file", displayPath, "ok", itemCount: scan.Total, failedCount: scan.Failed);
        }
        catch (Exception exception)
        {
            return Source(id, "file", displayPath, "parseFailed", error: exception.Message);
        }
    }

    private SteamLocalDataSourceDto CheckLocalConfigVdf(string? steamPath)
    {
        const string id = "file.localConfigVdf";
        var displayPath = steamPath == null
            ? null
            : Path.Combine(steamPath, "userdata", "*", "config", "localconfig.vdf");
        try
        {
            var count = 0;
            if (steamPath != null)
            {
                var userdataDir = Path.Combine(steamPath, "userdata");
                if (Directory.Exists(userdataDir))
                {
                    count = Directory.GetDirectories(userdataDir)
                        .Select(userDir => Path.Combine(userDir, "config", "localconfig.vdf"))
                        .Count(File.Exists);
                }
            }
            return count == 0
                ? Source(id, "file", displayPath, "missing", itemCount: 0)
                : Source(id, "file", displayPath, "ok", itemCount: count);
        }
        catch (Exception exception)
        {
            return Source(id, "file", displayPath, "parseFailed", error: exception.Message);
        }
    }

    private async Task<SteamLocalDataSourceDto> CheckAppRunningStatusJobAsync(CancellationToken cancellationToken)
    {
        const string id = "job.appRunningStatus";
        try
        {
            var enabled = settingsStore.GetSettings().UpdateAppRunningStatusJob?.Enabled == true;
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var recordCount = await db.UseAppRecordTable.CountAsync(cancellationToken);
            return enabled || recordCount > 0
                ? Source(id, "job", null, "ok", itemCount: recordCount)
                : Source(id, "job", null, "missing", itemCount: recordCount);
        }
        catch (Exception exception)
        {
            return Source(id, "job", null, "parseFailed", error: exception.Message);
        }
    }

    private static SteamLocalDataSourceDto Source(
        string id, string kind, string? path, string status,
        int? itemCount = null, int? failedCount = null, string? error = null)
        => new()
        {
            Id = id,
            Kind = kind,
            Path = path,
            Status = status,
            ItemCount = itemCount,
            FailedCount = failedCount,
            Error = error
        };
}
