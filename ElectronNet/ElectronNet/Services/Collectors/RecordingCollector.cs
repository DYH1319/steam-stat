using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ElectronNet.Helpers;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProtoBuf;
using SteamStat.Core.Archives.Recordings;
using SteamStat.Core.Helpers;
using SteamStat.Core.Platform;

namespace ElectronNet.Services.Collectors;

/// <summary>一个账号的录像文件夹里读到的东西。</summary>
internal sealed record RecordingScan(
    long AccountId,
    string SteamId,
    IReadOnlyList<RecordingTimeline> Timelines,
    IReadOnlyList<RecordingMediaFile> Media);

internal sealed record RecordingMediaFile(
    string MediaId,
    string Kind,
    int AppId,
    long StartedAt,
    long DurationMs,
    long SizeBytes,
    string? Name,
    IReadOnlyList<string> TimelineIds,
    string FolderPath,
    string? ThumbnailPath,
    IReadOnlyList<RecordingMarker> Markers);

/// <summary>
/// 只读扫描 Steam 录像目录。默认在 userdata/{accountId}/gamerecordings，
/// 用户可以在 Steam 设置里改到别处，改过的路径在 localconfig.vdf 的 GameRecording 段。
/// Steam 的文件绝不写回，读不懂的文件跳过。
/// </summary>
internal static partial class GameRecordingScanner
{
    /// <summary>Steam 的 DASH 分片固定 3 秒，文件夹里没有别的时长来源时用它估算。</summary>
    private const long SegmentMs = 3000;
    private const long MaxIndexBytes = 64 * 1024 * 1024;

    public static IReadOnlyList<RecordingScan> ScanAll(string steamPath, Action<Exception>? onError = null)
    {
        var userdata = Path.Combine(steamPath, "userdata");
        if (!Directory.Exists(userdata)) return [];
        var scans = new List<RecordingScan>();
        foreach (var userDir in Directory.EnumerateDirectories(userdata))
        {
            if (!long.TryParse(Path.GetFileName(userDir), NumberStyles.None, CultureInfo.InvariantCulture, out var accountId)
                || accountId is <= 0 or > int.MaxValue) continue;
            var steamId = SteamIdHelper.AccountIdToSteamId((int)accountId);
            if (steamId == null) continue;
            var root = ResolveRoot(userDir);
            if (root == null) continue;
            scans.Add(ScanAccount(root, accountId, steamId, onError));
        }
        return scans;
    }

    internal static string? ResolveRoot(string userDir)
    {
        var fallback = Path.Combine(userDir, "gamerecordings");
        var custom = ReadCustomPath(Path.Combine(userDir, "config", "localconfig.vdf"));
        if (custom != null)
        {
            if (LooksLikeRecordings(custom)) return custom;
            var nested = Path.Combine(custom, "gamerecordings");
            if (Directory.Exists(nested)) return nested;
        }
        return Directory.Exists(fallback) ? fallback : null;
    }

    private static bool LooksLikeRecordings(string path)
        => File.Exists(Path.Combine(path, "gamerecording.pb")) || Directory.Exists(Path.Combine(path, "clips")) || Directory.Exists(Path.Combine(path, "video"));

    /// <summary>键名没有文档，所以接受这一段里任何看起来像路径的值。</summary>
    internal static string? ReadCustomPath(string localConfig)
    {
        if (!File.Exists(localConfig)) return null;
        string text;
        try
        {
            text = File.ReadAllText(localConfig);
        }
        catch (IOException)
        {
            return null;
        }
        var block = RecordingBlock().Match(text);
        if (!block.Success) return null;
        foreach (Match entry in PathEntry().Matches(block.Groups[1].Value))
        {
            var value = entry.Groups[1].Value.Replace(@"\\", @"\", StringComparison.Ordinal);
            if (Directory.Exists(value)) return value;
        }
        return null;
    }

    internal static RecordingScan ScanAccount(string root, long accountId, string steamId, Action<Exception>? onError = null)
    {
        var timelines = ReadTimelines(root, onError);
        var byRecording = timelines.SelectMany(timeline => timeline.Videos.Select(video => (Video: video, Timeline: timeline)))
            .GroupBy(item => item.Video.RecordingId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var media = new List<RecordingMediaFile>();
        media.AddRange(ReadBackground(root, byRecording));
        media.AddRange(ReadClips(root, onError));
        return new RecordingScan(accountId, steamId, timelines, media);
    }

    private static IReadOnlyList<RecordingTimeline> ReadTimelines(string root, Action<Exception>? onError)
    {
        var timelines = new Dictionary<string, RecordingTimeline>(StringComparer.Ordinal);
        var index = Path.Combine(root, "gamerecording.pb");
        if (File.Exists(index) && new FileInfo(index).Length is > 0 and <= MaxIndexBytes)
        {
            try
            {
                using var stream = File.OpenRead(index);
                foreach (var timeline in GameRecordingFiles.ReadIndex(stream)) timelines[timeline.TimelineId] = timeline;
            }
            catch (Exception exception) when (exception is ProtoException or IOException or EndOfStreamException or InvalidOperationException)
            {
                onError?.Invoke(exception);
            }
        }

        var directory = Path.Combine(root, "timelines");
        if (!Directory.Exists(directory)) return timelines.Values.ToArray();
        foreach (var file in Directory.EnumerateFiles(directory, "timeline_*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (!GameRecordingFiles.TryParseTimelineId(id, out var appId, out var startedAt)) continue;
            try
            {
                var (end, markers) = GameRecordingFiles.ReadTimelineJson(File.ReadAllText(file));
                var known = timelines.GetValueOrDefault(id);
                timelines[id] = known == null
                    ? new RecordingTimeline(id, appId, startedAt, end ?? 0, [], markers)
                    : known with
                    {
                        DurationMs = known.DurationMs > 0 ? known.DurationMs : end ?? 0,
                        Markers = known.Markers.Concat(markers).Distinct().OrderBy(marker => marker.OffsetMs).ToArray()
                    };
            }
            catch (Exception exception) when (exception is JsonException or IOException)
            {
                onError?.Invoke(exception);
            }
        }
        return timelines.Values.ToArray();
    }

    private static IEnumerable<RecordingMediaFile> ReadBackground(string root, Dictionary<string, (RecordingVideo Video, RecordingTimeline Timeline)> byRecording)
    {
        var directory = Path.Combine(root, "video");
        if (!Directory.Exists(directory)) yield break;
        foreach (var folder in Directory.EnumerateDirectories(directory))
        {
            var name = Path.GetFileName(folder);
            if (!GameRecordingFiles.TryParseFolder(name, out _, out var appId, out var startedAt)) continue;
            var (size, chunks) = Measure(folder);
            byRecording.TryGetValue(name, out var known);
            var duration = known.Video?.DurationMs is > 0 ? known.Video.DurationMs : chunks * SegmentMs;
            yield return new RecordingMediaFile(
                name,
                RecordingKinds.Background,
                appId,
                startedAt,
                duration,
                size,
                null,
                known.Timeline == null ? [] : [known.Timeline.TimelineId],
                folder,
                null,
                []);
        }
    }

    private static IEnumerable<RecordingMediaFile> ReadClips(string root, Action<Exception>? onError)
    {
        var directory = Path.Combine(root, "clips");
        if (!Directory.Exists(directory)) yield break;
        foreach (var folder in Directory.EnumerateDirectories(directory))
        {
            var name = Path.GetFileName(folder);
            if (!GameRecordingFiles.TryParseFolder(name, out _, out var folderApp, out var folderTime)) continue;
            RecordingClip? clip = null;
            var file = Path.Combine(folder, "clip.pb");
            if (File.Exists(file))
            {
                try
                {
                    using var stream = File.OpenRead(file);
                    clip = GameRecordingFiles.ReadClip(stream, name);
                }
                catch (Exception exception) when (exception is ProtoException or IOException or EndOfStreamException or InvalidOperationException)
                {
                    onError?.Invoke(exception);
                }
            }
            var (size, chunks) = Measure(folder);
            var thumbnail = Path.Combine(folder, "thumbnail.jpg");
            yield return new RecordingMediaFile(
                name,
                RecordingKinds.Clip,
                clip?.AppId ?? folderApp,
                clip?.RecordedAt ?? folderTime,
                clip?.DurationMs is > 0 ? clip.DurationMs : chunks * SegmentMs,
                clip?.SizeBytes is > 0 ? clip.SizeBytes : size,
                clip?.Name,
                clip?.TimelineIds ?? [],
                folder,
                File.Exists(thumbnail) ? thumbnail : null,
                clip?.Markers ?? []);
        }
    }

    private static (long Size, long Chunks) Measure(string folder)
    {
        long size = 0, chunks = 0;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            size += new FileInfo(file).Length;
            if (Path.GetFileName(file).StartsWith("chunk-stream0-", StringComparison.OrdinalIgnoreCase)) chunks++;
        }
        return (size, chunks);
    }

    [GeneratedRegex(@"""GameRecording""\s*\{(.*?)\n\s*\}", RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex RecordingBlock();

    [GeneratedRegex(@"""[^""\r\n]*(?:path|folder|dir)[^""\r\n]*""\s+""([^""\r\n]+)""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PathEntry();
}

/// <summary>
/// Steam 录像档案。Steam 按空间自动清理录像，所以这里留下的是索引：
/// 视频没了，这一局的时间线标记还在。文件消失后保留记录，标记 MissingSince。
/// </summary>
internal sealed class RecordingCollector(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ISteamInstallLocator installLocator,
    TimeProvider timeProvider,
    ILogger<RecordingCollector> logger) : IArchiveCollector
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string Id => "recordings";

    public TimeSpan Interval => TimeSpan.FromHours(6);

    public async Task<CollectorOutcome> CollectAsync(CancellationToken cancellationToken)
    {
        var steamPath = installLocator.ReadSteamRegistry().SteamPath;
        if (string.IsNullOrWhiteSpace(steamPath)) return CollectorOutcome.Skipped;
        var failures = 0;
        var scans = await Task.Run(() => GameRecordingScanner.ScanAll(steamPath, exception =>
        {
            failures++;
            logger.LogInformation("Skipped an unreadable recording file with {ExceptionType}", exception.GetType().Name);
        }), cancellationToken).ConfigureAwait(false);
        var written = 0;
        foreach (var scan in scans) written += await SaveAsync(scan, cancellationToken).ConfigureAwait(false);
        return failures > 0 && written == 0 ? CollectorOutcome.Failed(CollectorErrors.Read) : CollectorOutcome.Ok(written);
    }

    internal async Task<int> SaveAsync(RecordingScan scan, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var timelines = await db.RecordingTimelineTable.Where(row => row.AccountId == scan.AccountId).ToDictionaryAsync(row => row.TimelineId, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        var written = 0;
        foreach (var timeline in scan.Timelines.Where(item => item.AppId > 0))
        {
            if (!timelines.TryGetValue(timeline.TimelineId, out var row))
            {
                row = new RecordingTimelineRecord { AccountId = scan.AccountId, TimelineId = timeline.TimelineId };
                db.RecordingTimelineTable.Add(row);
                timelines[timeline.TimelineId] = row;
                written++;
            }
            row.SteamId = scan.SteamId;
            row.AppId = timeline.AppId;
            row.StartedAt = timeline.StartedAt;
            row.DurationMs = Math.Max(row.DurationMs, timeline.DurationMs);
            // 只增不减：Steam 清理索引之后这些标记就只有这里有了
            row.Markers = Merge(row.Markers, timeline.Markers);
            row.IndexedAt = now;
        }

        var media = await db.RecordingMediaTable.Where(row => row.AccountId == scan.AccountId).ToDictionaryAsync(row => row.MediaId, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in scan.Media.Where(item => item.AppId > 0))
        {
            seen.Add(file.MediaId);
            if (!media.TryGetValue(file.MediaId, out var row))
            {
                row = new RecordingMediaRecord { AccountId = scan.AccountId, MediaId = file.MediaId, Kind = file.Kind };
                db.RecordingMediaTable.Add(row);
                media[file.MediaId] = row;
                written++;
            }
            row.SteamId = scan.SteamId;
            row.AppId = file.AppId;
            row.StartedAt = file.StartedAt;
            row.DurationMs = file.DurationMs;
            row.SizeBytes = file.SizeBytes;
            row.Name = file.Name ?? row.Name;
            row.TimelineIds = JsonSerializer.Serialize(file.TimelineIds);
            row.FolderPath = file.FolderPath;
            row.ThumbnailPath = file.ThumbnailPath;
            row.Markers = JsonSerializer.Serialize(file.Markers, JsonOptions);
            row.IndexedAt = now;
            row.MissingSince = null;
        }
        foreach (var row in media.Values.Where(row => row.MissingSince == null && !seen.Contains(row.MediaId))) row.MissingSince = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return written;
    }

    private static string Merge(string stored, IReadOnlyList<RecordingMarker> fresh)
    {
        var old = ParseMarkers(stored);
        return JsonSerializer.Serialize(old.Concat(fresh).Distinct().OrderBy(marker => marker.OffsetMs).ToArray(), JsonOptions);
    }

    internal static IReadOnlyList<RecordingMarker> ParseMarkers(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<RecordingMarker[]>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
