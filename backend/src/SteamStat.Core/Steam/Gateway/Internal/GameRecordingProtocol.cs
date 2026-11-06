using ProtoBuf;

namespace SteamStat.Core.Steam.Gateway.Internal;

// Steam 录像的本地文件格式，来自 SteamDatabase/Protobufs 的 webuimessages_gamerecordingfiles.proto。
// SteamKit2 没有这些类型。未知字段直接丢弃：这里只读不写，绝不回写 Steam 的文件。

[ProtoContract]
internal sealed class CGameRecordingFile
{
    [ProtoMember(1)] public List<TimelineMetadata> timelines { get; } = [];
    [ProtoMember(2)] public List<PostGameSummary> postgame_events { get; } = [];
    [ProtoMember(3)] public List<string> temporary_clips { get; } = [];
    [ProtoMember(4)] public List<RecordingTag> tags { get; } = [];
}

[ProtoContract]
internal sealed class CGameRecordingClipFile
{
    [ProtoMember(1)] public List<TimelineMetadata> timelines { get; } = [];
    [ProtoMember(2)] public ulong first_timeline_start_offset_ms { get; set; }
    [ProtoMember(3)] public uint date_recorded { get; set; }
    [ProtoMember(4)] public ulong game_id { get; set; }
    [ProtoMember(5, DataFormat = DataFormat.FixedSize)] public ulong published_file_id { get; set; }
    [ProtoMember(6)] public ulong size_in_bytes { get; set; }
    [ProtoMember(7)] public string name { get; set; } = string.Empty;
    [ProtoMember(8)] public bool temporary { get; set; }
    [ProtoMember(9)] public string original_device { get; set; } = string.Empty;
    [ProtoMember(14)] public List<RecordingTag> tags { get; } = [];
    [ProtoMember(15)] public List<RecordingPhase> phases { get; } = [];
}

[ProtoContract]
internal sealed class TimelineMetadata
{
    [ProtoMember(1)] public string timeline_id { get; set; } = string.Empty;
    [ProtoMember(2)] public ulong game_id { get; set; }
    [ProtoMember(3)] public uint date_recorded { get; set; }
    [ProtoMember(4)] public ulong duration_ms { get; set; }
    [ProtoMember(5)] public List<TimelineRecording> recordings { get; } = [];
    [ProtoMember(6)] public List<RecordingPhase> phases { get; } = [];
    [ProtoMember(7)] public List<TimelineEvent> significant_events { get; } = [];
}

[ProtoContract]
internal sealed class TimelineRecording
{
    [ProtoMember(1)] public string recording_id { get; set; } = string.Empty;
    [ProtoMember(2)] public ulong start_offset_ms { get; set; }
    [ProtoMember(3)] public ulong duration_ms { get; set; }
    [ProtoMember(4)] public int recording_type { get; set; }
    [ProtoMember(9)] public ulong file_size { get; set; }
}

[ProtoContract]
internal sealed class PostGameSummary
{
    [ProtoMember(1)] public ulong game_id { get; set; }
    [ProtoMember(2)] public List<TimelineEvent> events { get; } = [];
}

[ProtoContract]
internal sealed class TimelineEvent
{
    [ProtoMember(1)] public ulong game_id { get; set; }
    [ProtoMember(2)] public uint rt_created { get; set; }
    [ProtoMember(4)] public string timeline_id { get; set; } = string.Empty;
    [ProtoMember(5)] public ulong entry_id { get; set; }
    [ProtoMember(6)] public ulong timeline_offset_ms { get; set; }
    [ProtoMember(7)] public ulong duration_ms { get; set; }
    [ProtoMember(8)] public string marker_icon { get; set; } = string.Empty;
    [ProtoMember(9)] public string marker_title { get; set; } = string.Empty;
    [ProtoMember(10)] public bool user_marker { get; set; }
}

[ProtoContract]
internal sealed class RecordingTag
{
    [ProtoMember(1)] public ulong game_id { get; set; }
    [ProtoMember(2)] public TimelineTag? tag { get; set; }
    [ProtoMember(3)] public List<TagReference> references { get; } = [];
}

[ProtoContract]
internal sealed class TagReference
{
    [ProtoMember(1)] public string clip_id { get; set; } = string.Empty;
    [ProtoMember(2)] public string timeline_id { get; set; } = string.Empty;
    [ProtoMember(3)] public ulong offset_ms { get; set; }
}

[ProtoContract]
internal sealed class TimelineTag
{
    [ProtoMember(1)] public string name { get; set; } = string.Empty;
    [ProtoMember(2)] public string group { get; set; } = string.Empty;
    [ProtoMember(3)] public string icon { get; set; } = string.Empty;
    [ProtoMember(4)] public uint priority { get; set; }
}

[ProtoContract]
internal sealed class RecordingPhase
{
    [ProtoMember(4)] public string phase_id { get; set; } = string.Empty;
    [ProtoMember(5)] public ulong duration_ms { get; set; }
    [ProtoMember(6)] public List<PhaseTag> tags { get; } = [];
    [ProtoMember(7)] public List<PhaseTag> contained_tags { get; } = [];
    [ProtoMember(8)] public ulong background_timeline_offset { get; set; }
    [ProtoMember(9)] public List<PhaseAttribute> attributes { get; } = [];
}

[ProtoContract]
internal sealed class PhaseTag
{
    [ProtoMember(1)] public string name { get; set; } = string.Empty;
    [ProtoMember(2)] public string group { get; set; } = string.Empty;
}

[ProtoContract]
internal sealed class PhaseAttribute
{
    [ProtoMember(1)] public string group { get; set; } = string.Empty;
    [ProtoMember(2)] public string value { get; set; } = string.Empty;
    [ProtoMember(3)] public uint priority { get; set; }
}
