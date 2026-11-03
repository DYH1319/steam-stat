using System.Globalization;
using ElectronNet.Helpers;
using SteamStat.Core.Helpers;
using ValveKeyValue;

namespace ElectronNet.Features.Archive;

/// <summary>发现的存档目录候选</summary>
public sealed record SaveLocationCandidate(long AppId, string SteamId, string Kind, string Path, int FileCount);

/// <summary>remotecache.vdf 中的一个云同步文件</summary>
public sealed record RemoteCacheEntry(string RelativePath, int Root);

/// <summary>
/// 存档目录定位（只读）：
/// 1. Steam 云存档：{SteamPath}/userdata/{accountId}/{appId}/remote（ISteamRemoteStorage 写入的文件）；
/// 2. Auto-Cloud：读取同目录 remotecache.vdf，按 root（文档、AppData、LocalLow、Saved Games…）还原文件真实位置，
///    取这些文件的公共父目录作为候选（推断可能不完全准确，需要用户确认）。
/// </summary>
public static class SaveLocationDiscovery
{
    /// <summary>Steam 客户端自身、截图（760）与控制器配置（241100）不是游戏存档。</summary>
    public static readonly IReadOnlySet<long> ExcludedAppIds = new HashSet<long> { 0, 7, 760, 241100 };

    public static IReadOnlyList<SaveLocationCandidate> Discover(string steamPath, Func<long, string?> installDirectory)
    {
        var userdata = Path.Combine(steamPath, "userdata");
        if (!Directory.Exists(userdata)) return [];
        var result = new List<SaveLocationCandidate>();
        foreach (var userDir in Directory.EnumerateDirectories(userdata))
        {
            if (!long.TryParse(Path.GetFileName(userDir), NumberStyles.None, CultureInfo.InvariantCulture, out var accountId)
                || accountId is <= 0 or > int.MaxValue) continue;
            var steamId = SteamIdHelper.AccountIdToSteamId((int)accountId);
            if (steamId == null) continue;
            foreach (var appDir in Directory.EnumerateDirectories(userDir))
            {
                if (!long.TryParse(Path.GetFileName(appDir), NumberStyles.None, CultureInfo.InvariantCulture, out var appId)
                    || appId > int.MaxValue || ExcludedAppIds.Contains(appId)) continue;
                var remote = Path.Combine(appDir, "remote");
                var remoteFiles = CountFiles(remote);
                if (remoteFiles > 0)
                    result.Add(new SaveLocationCandidate(appId, steamId, "steamCloud", Path.GetFullPath(remote), remoteFiles));
                var cache = Path.Combine(appDir, "remotecache.vdf");
                if (!File.Exists(cache)) continue;
                IReadOnlyList<RemoteCacheEntry> entries;
                try
                {
                    entries = ParseRemoteCache(VdfHelper.Read(cache));
                }
                catch
                {
                    continue;
                }
                foreach (var candidate in InferAutoCloud(entries, root => ResolveRoot(root, appId, installDirectory)))
                    result.Add(new SaveLocationCandidate(appId, steamId, "autoCloud", candidate.Path, candidate.FileCount));
            }
        }
        return result;
    }

    public static IReadOnlyList<RemoteCacheEntry> ParseRemoteCache(KVObject root)
    {
        var entries = new List<RemoteCacheEntry>();
        foreach (var child in root.Children)
        {
            if (!child.Children.Any()) continue;
            var rootValue = child.Children.FirstOrDefault(item => string.Equals(item.Name, "root", StringComparison.OrdinalIgnoreCase))
                ?.Value?.ToString(CultureInfo.InvariantCulture);
            if (!int.TryParse(rootValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rootId)) continue;
            entries.Add(new RemoteCacheEntry(child.Name.Replace('\\', '/'), rootId));
        }
        return entries;
    }

    /// <summary>
    /// 每个 root 取文件的公共父目录；公共目录为空（文件直接放在文档 / AppData 根目录）时不推荐，避免备份整个用户目录。
    /// </summary>
    public static IEnumerable<(string Path, int FileCount)> InferAutoCloud(
        IEnumerable<RemoteCacheEntry> entries,
        Func<int, string?> resolveRoot)
    {
        foreach (var group in entries.Where(entry => entry.Root != 0).GroupBy(entry => entry.Root))
        {
            var rootPath = resolveRoot(group.Key);
            if (string.IsNullOrWhiteSpace(rootPath)) continue;
            var prefix = CommonDirectory(group.Select(entry => entry.RelativePath));
            if (prefix.Length == 0 || prefix.Split('/').Any(segment => segment is ".." or ".")) continue;
            string full;
            try
            {
                full = Path.GetFullPath(Path.Combine(rootPath, prefix.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            if (!Directory.Exists(full)) continue;
            yield return (full, group.Count());
        }
    }

    public static string CommonDirectory(IEnumerable<string> relativePaths)
    {
        string[]? common = null;
        foreach (var path in relativePaths)
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var directories = segments.Length > 1 ? segments[..^1] : [];
            if (common == null)
            {
                common = directories;
                continue;
            }
            var length = 0;
            while (length < common.Length && length < directories.Length
                   && string.Equals(common[length], directories[length], StringComparison.OrdinalIgnoreCase)) length++;
            common = common[..length];
            if (common.Length == 0) break;
        }
        return common == null ? string.Empty : string.Join('/', common);
    }

    /// <summary>ERemoteStorageFileRoot → 本机目录（只覆盖 Windows 与 Linux 原生游戏的常见位置）。</summary>
    public static string? ResolveRoot(int root, long appId, Func<long, string?> installDirectory)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            return root switch
            {
                1 => installDirectory(appId),
                2 => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                3 => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                4 => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                9 => string.IsNullOrEmpty(home) ? null : Path.Combine(home, "Saved Games"),
                10 => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                12 => string.IsNullOrEmpty(home) ? null : Path.Combine(home, "AppData", "LocalLow"),
                _ => null
            };
        }
        if (OperatingSystem.IsLinux())
        {
            return root switch
            {
                1 => installDirectory(appId),
                14 => home,
                15 => Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data ? data : Path.Combine(home, ".local", "share"),
                16 => Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config ? config : Path.Combine(home, ".config"),
                _ => null
            };
        }
        return null;
    }

    private static int CountFiles(string directory)
    {
        if (!Directory.Exists(directory)) return 0;
        try
        {
            return Directory.EnumerateFiles(directory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }).Take(100_000).Count();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
