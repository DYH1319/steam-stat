using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProtoBuf;
using SteamStat.Core.Steam.Gateway.Internal;

namespace SteamStat.Core.Archives.Recordings;

public static class RecordingKinds
{
    public const string Background = "background";
    public const string Clip = "clip";
}

/// <summary>时间线上的一个标记。OffsetMs 是相对时间线开始的毫秒数。</summary>
public sealed record RecordingMarker(string Kind, string Title, string? Group, string? Icon, long OffsetMs, long? DurationMs);

/// <summary>一条时间线：一次游戏过程。视频可能已经被 Steam 清理，这些标记还留着。</summary>
public sealed record RecordingTimeline(
    string TimelineId,
    int AppId,
    long StartedAt,
    long DurationMs,
    IReadOnlyList<RecordingVideo> Videos,
    IReadOnlyList<RecordingMarker> Markers);

public sealed record RecordingVideo(string RecordingId, long StartOffsetMs, long DurationMs, long FileSize);

/// <summary>一个保存下来的片段。</summary>
public sealed record RecordingClip(
    string ClipId,
    int AppId,
    string? Name,
    long RecordedAt,
    long DurationMs,
    long SizeBytes,
    IReadOnlyList<string> TimelineIds,
    IReadOnlyList<RecordingMarker> Markers);

/// <summary>
/// 读 userdata/{accountId}/gamerecordings 下的文件，都是只读的：
/// gamerecording.pb（时间线和标签索引）、clips/clip_*/clip.pb、timelines/timeline_*.json。
/// 文件名里的时间是 UTC（用真实档案核对过：timeline_…_050839 对应本地 13:08:39 启动的那一局，时区 +8）。
/// </summary>
public static partial class GameRecordingFiles
{
    /// <summary>timeline_{appid}{yyyyMMdd}_{HHmmss}，应用 ID 和日期之间没有分隔符。</summary>
    public static bool TryParseTimelineId(string name, out int appId, out long startedAt)
    {
        appId = 0;
        startedAt = 0;
        var match = TimelineName().Match(name);
        return match.Success
               && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out appId)
               && TryParseStamp(match.Groups[2].Value, out startedAt);
    }

    /// <summary>bg_{appid}_{yyyyMMdd}_{HHmmss}、fg_…、clip_…。</summary>
    public static bool TryParseFolder(string name, out string kind, out int appId, out long startedAt)
    {
        kind = string.Empty;
        appId = 0;
        startedAt = 0;
        var match = FolderName().Match(name);
        if (!match.Success
            || !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out appId)
            || !TryParseStamp(match.Groups[3].Value, out startedAt)) return false;
        kind = match.Groups[1].Value.Equals("clip", StringComparison.OrdinalIgnoreCase) ? RecordingKinds.Clip : RecordingKinds.Background;
        return true;
    }

    private static bool TryParseStamp(string stamp, out long unix)
    {
        unix = 0;
        if (!DateTime.TryParseExact(stamp, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)) return false;
        unix = new DateTimeOffset(value, TimeSpan.Zero).ToUnixTimeSeconds();
        return true;
    }

    /// <summary>
    /// 读索引。Valve 没有承诺这个格式，读不了时抛 ProtoException，调用方记下失败继续扫其他东西。
    /// 标签是按「标签 → 时间线」存的，这里翻成「时间线 → 标记」。
    /// </summary>
    public static IReadOnlyList<RecordingTimeline> ReadIndex(Stream stream)
    {
        var file = Serializer.Deserialize<CGameRecordingFile>(stream);
        var markers = new Dictionary<string, List<RecordingMarker>>(StringComparer.Ordinal);
        foreach (var entry in file.tags)
        {
            if (entry.tag == null) continue;
            foreach (var reference in entry.references)
            {
                if (reference.timeline_id.Length == 0) continue;
                Add(markers, reference.timeline_id, new RecordingMarker("tag", entry.tag.name, Blank(entry.tag.group), Blank(entry.tag.icon), (long)reference.offset_ms, null));
            }
        }

        var timelines = new Dictionary<string, RecordingTimeline>(StringComparer.Ordinal);
        foreach (var timeline in file.timelines)
        {
            if (timeline.timeline_id.Length == 0) continue;
            foreach (var marker in ReadEvents(timeline.significant_events)) Add(markers, timeline.timeline_id, marker);
            foreach (var marker in ReadPhases(timeline.phases)) Add(markers, timeline.timeline_id, marker);
            timelines[timeline.timeline_id] = Build(timeline, markers.GetValueOrDefault(timeline.timeline_id));
        }
        foreach (var summary in file.postgame_events)
        {
            foreach (var marker in summary.events.Where(item => item.timeline_id.Length > 0))
                Add(markers, marker.timeline_id, ToMarker(marker));
        }

        // 只有标签、没有时间线元数据时（本机实测的样本就是这样），时间和应用靠名字解析
        foreach (var (id, list) in markers)
        {
            if (timelines.TryGetValue(id, out var known))
            {
                timelines[id] = known with { Markers = Dedupe(list) };
                continue;
            }
            if (!TryParseTimelineId(id, out var appId, out var startedAt)) continue;
            timelines[id] = new RecordingTimeline(id, appId, startedAt, 0, [], Dedupe(list));
        }
        return timelines.Values.OrderBy(item => item.StartedAt).ToArray();
    }

    /// <summary>clips/clip_*/clip.pb。</summary>
    public static RecordingClip? ReadClip(Stream stream, string clipId)
    {
        var file = Serializer.Deserialize<CGameRecordingClipFile>(stream);
        _ = TryParseFolder(clipId, out _, out var folderApp, out var folderTime);
        var appId = AppIdOf(file.game_id) ?? folderApp;
        if (appId <= 0) return null;
        var recordedAt = file.date_recorded > 0 ? file.date_recorded : folderTime;
        var duration = file.timelines.SelectMany(timeline => timeline.recordings).Sum(recording => (long)recording.duration_ms);
        if (duration == 0) duration = file.timelines.Sum(timeline => (long)timeline.duration_ms);
        var markers = new List<RecordingMarker>();
        foreach (var entry in file.tags.Where(item => item.tag != null))
            markers.Add(new RecordingMarker("tag", entry.tag!.name, Blank(entry.tag.group), Blank(entry.tag.icon), 0, null));
        markers.AddRange(ReadPhases(file.phases));
        foreach (var timeline in file.timelines) markers.AddRange(ReadEvents(timeline.significant_events));
        return new RecordingClip(
            clipId,
            appId,
            Blank(file.name),
            recordedAt,
            duration,
            (long)file.size_in_bytes,
            file.timelines.Select(timeline => timeline.timeline_id).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToArray(),
            Dedupe(markers));
    }

    /// <summary>
    /// timelines/timeline_*.json：{ daterecorded, endtime, entries: [{ id, time, type, title, ... }] }。
    /// 数字大多写成字符串。没有 time 的条目跳过。
    /// </summary>
    public static (long? EndMs, IReadOnlyList<RecordingMarker> Markers) ReadTimelineJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return (null, []);
        var end = Number(root, "endtime");
        if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) return ((long?)end, []);
        var markers = new List<RecordingMarker>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || Number(entry, "time") is not { } time) continue;
            var title = Field(entry, "title") ?? Field(entry, "achievement_name") ?? Field(entry, "description") ?? string.Empty;
            // 「#GameRecording_RecordingFailed」这样的本地化键没有意义，保留类型就够了
            markers.Add(new RecordingMarker(
                Field(entry, "type") ?? "unknown",
                title.StartsWith('#') ? string.Empty : title,
                null,
                Field(entry, "icon"),
                (long)time,
                Number(entry, "duration") is { } duration ? (long)duration : null));
        }
        return ((long?)end, markers);
    }

    /// <summary>game_id 的高位不为 0 时是非 Steam 游戏或模组，低 24 位不是应用 ID。</summary>
    private static int? AppIdOf(ulong gameId) => gameId is > 0 and <= 0xFFFFFF ? (int)gameId : null;

    private static RecordingTimeline Build(TimelineMetadata timeline, List<RecordingMarker>? markers)
    {
        _ = TryParseTimelineId(timeline.timeline_id, out var nameApp, out var nameTime);
        var videos = timeline.recordings
            .Where(recording => recording.recording_id.Length > 0)
            .Select(recording => new RecordingVideo(recording.recording_id, (long)recording.start_offset_ms, (long)recording.duration_ms, (long)recording.file_size))
            .ToArray();
        return new RecordingTimeline(
            timeline.timeline_id,
            AppIdOf(timeline.game_id) ?? nameApp,
            timeline.date_recorded > 0 ? timeline.date_recorded : nameTime,
            (long)timeline.duration_ms,
            videos,
            markers ?? []);
    }

    private static IEnumerable<RecordingMarker> ReadEvents(IEnumerable<TimelineEvent> events) => events.Select(ToMarker);

    private static RecordingMarker ToMarker(TimelineEvent item)
        => new(item.user_marker ? "usermarker" : "event", item.marker_title, null, Blank(item.marker_icon), (long)item.timeline_offset_ms, item.duration_ms > 0 ? (long)item.duration_ms : null);

    private static IEnumerable<RecordingMarker> ReadPhases(IEnumerable<RecordingPhase> phases)
    {
        foreach (var phase in phases)
        {
            var title = phase.attributes.OrderByDescending(item => item.priority).Select(item => item.value).FirstOrDefault(value => value.Length > 0)
                ?? phase.tags.Select(item => item.name).FirstOrDefault(name => name.Length > 0);
            if (title == null) continue;
            yield return new RecordingMarker("phase", title, phase.attributes.Select(item => Blank(item.group)).FirstOrDefault(group => group != null), null, (long)phase.background_timeline_offset, phase.duration_ms > 0 ? (long)phase.duration_ms : null);
        }
    }

    private static IReadOnlyList<RecordingMarker> Dedupe(IEnumerable<RecordingMarker> markers)
        => markers.Where(item => item.Title.Length > 0 || item.Kind != "tag").Distinct().OrderBy(item => item.OffsetMs).ToArray();

    private static void Add(Dictionary<string, List<RecordingMarker>> markers, string timelineId, RecordingMarker marker)
    {
        if (!markers.TryGetValue(timelineId, out var list)) markers[timelineId] = list = [];
        list.Add(marker);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? Field(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? Blank(value.GetString()) : null;

    private static double? Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    [GeneratedRegex(@"^timeline_(\d+?)_?(\d{8}_\d{6})(?:\.json)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TimelineName();

    [GeneratedRegex(@"^(bg|fg|clip)_(\d+)_(\d{8}_\d{6})$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex FolderName();
}
