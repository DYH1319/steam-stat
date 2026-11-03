using System.Globalization;
using SteamStat.Core.Helpers;

namespace ElectronNet.Features.SaveAnalysis;

/// <summary>按清单定位到的一个原始存档文件</summary>
public sealed record LocatedSave(string Path, string Name, SaveAnalysisLocation Location, string? SteamId, long Size, long ModifiedAt);

/// <summary>定位所需的本机环境（便于测试替换）</summary>
public sealed record SaveLocatorEnvironment(
    string? SteamPath,
    Func<string, IReadOnlyList<string>> SpecialFolders,
    Func<int, string?> InstallDirectory);

/// <summary>
/// 按插件清单的 locations 只读枚举原始存档文件：不递归、不跟随目录联接点，只返回匹配文件名通配的普通文件。
/// </summary>
public static class SaveAnalysisLocator
{
    public static IReadOnlyList<LocatedSave> Locate(SaveAnalysisManifest manifest, SaveLocatorEnvironment environment)
    {
        var result = new List<LocatedSave>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var location in manifest.Locations)
        {
            foreach (var (directory, steamId) in ResolveDirectories(location, manifest, environment))
            {
                if (!Directory.Exists(directory)) continue;
                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(directory, "*", new EnumerationOptions
                    {
                        RecurseSubdirectories = false,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Directory
                    }).ToList();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                foreach (var file in files)
                {
                    var name = System.IO.Path.GetFileName(file);
                    if (!location.Files.Any(pattern => SaveAnalysisManifestLoader.MatchesPattern(name, pattern))) continue;
                    if (!seen.Add(System.IO.Path.GetFullPath(file))) continue;
                    var info = new FileInfo(file);
                    if (!info.Exists) continue;
                    result.Add(new LocatedSave(
                        info.FullName,
                        name,
                        location,
                        steamId,
                        info.Length,
                        new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds()));
                }
            }
        }
        return result.OrderByDescending(item => item.ModifiedAt).ToArray();
    }

    private static IEnumerable<(string Directory, string? SteamId)> ResolveDirectories(
        SaveAnalysisLocation location,
        SaveAnalysisManifest manifest,
        SaveLocatorEnvironment environment)
    {
        var relative = location.Path.Replace('/', System.IO.Path.DirectorySeparatorChar);
        if (location.Root == "steamUserdata")
        {
            if (string.IsNullOrWhiteSpace(environment.SteamPath)) yield break;
            var userdata = System.IO.Path.Combine(environment.SteamPath, "userdata");
            if (!Directory.Exists(userdata)) yield break;
            foreach (var userDir in Directory.EnumerateDirectories(userdata))
            {
                if (!int.TryParse(System.IO.Path.GetFileName(userDir), NumberStyles.None, CultureInfo.InvariantCulture, out var accountId)
                    || accountId <= 0) continue;
                yield return (System.IO.Path.Combine(userDir, relative), SteamIdHelper.AccountIdToSteamId(accountId));
            }
            yield break;
        }
        if (location.Root == "gameInstall")
        {
            foreach (var appId in manifest.AppIds)
            {
                var install = environment.InstallDirectory(appId);
                if (!string.IsNullOrWhiteSpace(install)) yield return (System.IO.Path.Combine(install, relative), null);
            }
            yield break;
        }
        foreach (var root in environment.SpecialFolders(location.Root))
            if (!string.IsNullOrWhiteSpace(root)) yield return (System.IO.Path.Combine(root, relative), null);
    }

    /// <summary>
    /// 当前系统的特殊目录。「文档」被重定向（如移到 D 盘）时，不少游戏仍写入 %USERPROFILE%\Documents，两处都查。
    /// </summary>
    public static IReadOnlyList<string> SystemFolders(string root)
    {
        var candidates = root == "documents"
            ? [Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents")]
            : new[] { SystemFolder(root) };
        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => System.IO.Path.GetFullPath(path!))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();
    }

    private static string? SystemFolder(string root)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return root switch
        {
            "documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "appData" => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "localAppData" => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "localLow" => OperatingSystem.IsWindows() && home.Length > 0 ? System.IO.Path.Combine(home, "AppData", "LocalLow") : null,
            "savedGames" => OperatingSystem.IsWindows() && home.Length > 0 ? System.IO.Path.Combine(home, "Saved Games") : null,
            "home" => home,
            "xdgData" => Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data ? data : System.IO.Path.Combine(home, ".local", "share"),
            "xdgConfig" => Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config ? config : System.IO.Path.Combine(home, ".config"),
            _ => null
        };
    }

    /// <summary>用户手动选择文件时按 detect 规则匹配插件：文件名通配（未声明则按 locations）+ 文件头。</summary>
    public static bool Detects(SaveAnalysisManifest manifest, string fileName, ReadOnlySpan<byte> head)
    {
        var patterns = manifest.Detect?.Files is { Count: > 0 } declared
            ? declared
            : manifest.Locations.SelectMany(location => location.Files).ToArray();
        if (patterns.Count == 0 || !patterns.Any(pattern => SaveAnalysisManifestLoader.MatchesPattern(fileName, pattern))) return false;
        var magic = manifest.Detect?.Magic;
        if (string.IsNullOrEmpty(magic)) return true;
        var bytes = System.Text.Encoding.ASCII.GetBytes(magic);
        return head.Length >= bytes.Length && head[..bytes.Length].SequenceEqual(bytes);
    }
}
