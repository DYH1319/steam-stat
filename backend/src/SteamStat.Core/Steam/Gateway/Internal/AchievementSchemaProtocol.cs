using ProtoBuf;

namespace SteamStat.Core.Steam.Gateway.Internal;

internal static class AchievementSchemaProtocol
{
    public const string ServiceMethod = "Player.GetGameAchievements#1";
}

[ProtoContract]
internal sealed class AchievementSchemaRequest : IExtensible
{
    private IExtension? _extensionData;
    IExtension IExtensible.GetExtensionObject(bool createIfMissing)
        => Extensible.GetExtensionObject(ref _extensionData, createIfMissing);
    [ProtoMember(1)] public uint appid { get; set; }
    [ProtoMember(2)] public string language { get; set; } = string.Empty;
    [ProtoMember(3)] public bool hash_only { get; set; }
}

[ProtoContract]
internal sealed class AchievementSchemaResponse : IExtensible
{
    private IExtension? _extensionData;
    IExtension IExtensible.GetExtensionObject(bool createIfMissing)
        => Extensible.GetExtensionObject(ref _extensionData, createIfMissing);
    [ProtoMember(1)] public List<Achievement> achievements { get; } = [];
    [ProtoMember(2)] public int? schema_version { get; set; }
    [ProtoMember(3)] public List<Group> groups { get; } = [];
    [ProtoMember(4)] public uint? schema_hash { get; set; }

    [ProtoContract]
    internal sealed class Achievement : IExtensible
    {
        private IExtension? _extensionData;
        IExtension IExtensible.GetExtensionObject(bool createIfMissing)
            => Extensible.GetExtensionObject(ref _extensionData, createIfMissing);
        [ProtoMember(1)] public string internal_name { get; set; } = string.Empty;
        [ProtoMember(2)] public string localized_name { get; set; } = string.Empty;
        [ProtoMember(3)] public string localized_desc { get; set; } = string.Empty;
        [ProtoMember(4)] public string icon { get; set; } = string.Empty;
        [ProtoMember(5)] public string icon_gray { get; set; } = string.Empty;
        [ProtoMember(6)] public bool hidden { get; set; }
        [ProtoMember(7)] public string player_percent_unlocked { get; set; } = string.Empty;
        [ProtoMember(8)] public uint? internal_key { get; set; }
        [ProtoMember(9)] public int? min_progress_int { get; set; }
        [ProtoMember(10)] public int? max_progress_int { get; set; }
        [ProtoMember(11)] public uint? groupid { get; set; }
        [ProtoMember(12)] public bool archived { get; set; }
        [ProtoMember(13)] public int progress_type { get; set; }
        [ProtoMember(14)] public float? min_progress_float { get; set; }
        [ProtoMember(15)] public float? max_progress_float { get; set; }
    }

    [ProtoContract]
    internal sealed class Group : IExtensible
    {
        private IExtension? _extensionData;
        IExtension IExtensible.GetExtensionObject(bool createIfMissing)
            => Extensible.GetExtensionObject(ref _extensionData, createIfMissing);
        [ProtoMember(1)] public uint groupid { get; set; }
        [ProtoMember(2)] public string localized_name { get; set; } = string.Empty;
        [ProtoMember(3)] public uint? dlcappid { get; set; }
        [ProtoMember(4)] public bool archived { get; set; }
        [ProtoMember(5)] public bool developeronly { get; set; }
        [ProtoMember(6)] public uint order { get; set; }
        [ProtoMember(7)] public bool ispublic { get; set; }
        [ProtoMember(8)] public uint? total_achievements { get; set; }
        [ProtoMember(9)] public uint? completion_achievements { get; set; }
    }
}
