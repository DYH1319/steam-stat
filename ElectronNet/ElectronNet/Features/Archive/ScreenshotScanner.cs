using System.Globalization;
using ElectronNet.Helpers;
using SteamStat.Core.Helpers;
using ValveKeyValue;

namespace ElectronNet.Features.Archive;

/// <summary>扫描到的一张 Steam 截图</summary>
public sealed record ScreenshotFileInfo(
    long AccountId,
    string SteamId,
    long AppId,
    string FileName,
    string FilePath,
    string? ThumbnailPath,
    long TakenAt,
    long FileSize,
    int? Width,
    int? Height,
    string? Caption);

/// <summary>screenshots.vdf 中的一条元数据</summary>
public sealed record ScreenshotMetadata(long? CreatedAt, int? Width, int? Height, string? Caption);

/// <summary>
/// 只读扫描 {SteamPath}/userdata/{accountId}/760/remote/{appId}/screenshots：
/// 文件系统是截图是否存在的唯一依据，screenshots.vdf 只补充拍摄时间、分辨率与说明。
/// </summary>
public static class ScreenshotScanner
{
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png"];

    /// <param name="onScanned">每发现一张截图回调累计数量</param>
    public static IReadOnlyList<ScreenshotFileInfo> ScanAll(string steamPath, Action<int>? onScanned = null)
    {
        var userdata = Path.Combine(steamPath, "userdata");
        if (!Directory.Exists(userdata)) return [];
        var result = new List<ScreenshotFileInfo>();
        foreach (var userDir in Directory.EnumerateDirectories(userdata))
        {
            if (!long.TryParse(Path.GetFileName(userDir), NumberStyles.None, CultureInfo.InvariantCulture, out var accountId)
                || accountId is <= 0 or > int.MaxValue) continue;
            var steamId = SteamIdHelper.AccountIdToSteamId((int)accountId);
            if (steamId == null) continue;
            foreach (var file in ScanAccount(Path.Combine(userDir, "760"), accountId, steamId))
            {
                result.Add(file);
                onScanned?.Invoke(result.Count);
            }
        }
        return result;
    }

    public static IEnumerable<ScreenshotFileInfo> ScanAccount(string screenshotRoot, long accountId, string steamId)
    {
        var remote = Path.Combine(screenshotRoot, "remote");
        if (!Directory.Exists(remote)) yield break;
        var metadata = ReadMetadata(Path.Combine(screenshotRoot, "screenshots.vdf"));
        foreach (var appDir in Directory.EnumerateDirectories(remote))
        {
            var appFolder = Path.GetFileName(appDir);
            // 非 Steam 快捷方式的 gameid 超出 int 范围，不在本软件范围内
            if (!long.TryParse(appFolder, NumberStyles.None, CultureInfo.InvariantCulture, out var appId)
                || appId is <= 0 or > int.MaxValue) continue;
            var shots = Path.Combine(appDir, "screenshots");
            if (!Directory.Exists(shots)) continue;
            var thumbnails = Path.Combine(shots, "thumbnails");
            foreach (var file in Directory.EnumerateFiles(shots))
            {
                if (!ImageExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
                FileInfo info;
                try
                {
                    info = new FileInfo(file);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                var fileName = info.Name;
                metadata.TryGetValue($"{appFolder}/screenshots/{fileName}", out var meta);
                var thumbnail = Path.Combine(thumbnails, fileName);
                yield return new ScreenshotFileInfo(
                    accountId,
                    steamId,
                    appId,
                    fileName,
                    info.FullName,
                    File.Exists(thumbnail) ? thumbnail : null,
                    meta?.CreatedAt ?? ParseTakenAt(fileName) ?? new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds(),
                    info.Length,
                    meta?.Width,
                    meta?.Height,
                    string.IsNullOrWhiteSpace(meta?.Caption) ? null : meta.Caption);
            }
        }
    }

    /// <summary>Steam 截图文件名形如 20240101123456_1.jpg（本地时间）。</summary>
    public static long? ParseTakenAt(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var underscore = stem.IndexOf('_');
        var stamp = underscore > 0 ? stem[..underscore] : stem;
        if (stamp.Length != 14
            || !DateTime.TryParseExact(stamp, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local))
            return null;
        return new DateTimeOffset(local).ToUnixTimeSeconds();
    }

    /// <summary>读取 screenshots.vdf，键为相对 remote 目录的文件路径（/ 分隔）。解析失败时返回空表。</summary>
    public static Dictionary<string, ScreenshotMetadata> ReadMetadata(string file)
    {
        var result = new Dictionary<string, ScreenshotMetadata>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(file)) return result;
        try
        {
            return ParseMetadata(VdfHelper.Read(file));
        }
        catch
        {
            return result;
        }
    }

    public static Dictionary<string, ScreenshotMetadata> ParseMetadata(KVObject root)
    {
        var result = new Dictionary<string, ScreenshotMetadata>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in root.Children)
        {
            foreach (var entry in app.Children)
            {
                var fileName = ReadString(entry, "filename");
                if (string.IsNullOrWhiteSpace(fileName)) continue;
                var created = ReadLong(entry, "creation");
                result[fileName.Replace('\\', '/')] = new ScreenshotMetadata(
                    created is > 0 ? created : null,
                    (int?)ReadLong(entry, "width"),
                    (int?)ReadLong(entry, "height"),
                    ReadString(entry, "caption"));
            }
        }
        return result;
    }

    private static string? ReadString(KVObject parent, string name)
    {
        var child = parent.Children.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        return child?.Value?.ToString(CultureInfo.InvariantCulture);
    }

    private static long? ReadLong(KVObject parent, string name)
    {
        var text = ReadString(parent, name);
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
