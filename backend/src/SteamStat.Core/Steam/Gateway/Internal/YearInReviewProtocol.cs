using ProtoBuf;
using SteamKit2;
using SteamKit2.Internal;

namespace SteamStat.Core.Steam.Gateway.Internal;

// Steam 年度回顾（SaleFeature.GetUserYearInReview），来自 SteamDatabase/Protobufs 的 webui/service_salefeature.proto。
// SteamKit2 没有这些类型。只声明用得到的字段，其余字段经 IExtensible 原样保留：
// 序列化回去就是原始回复，存下来以后补上字段可以重新解析。

internal static class YearInReviewProtocol
{
    public const string Service = "SaleFeature";
    public const string GetUserYearInReviewMethod = "GetUserYearInReview";
    public const string GetUserYearInReview = Service + "." + GetUserYearInReviewMethod + "#1";
}

[ProtoContract]
internal sealed class CSaleFeature_GetUserYearInReview_Request : IExtensible
{
    private IExtension? _extensionData;
    IExtension IExtensible.GetExtensionObject(bool createIfMissing) => Extensible.GetExtensionObject(ref _extensionData, createIfMissing);
    [ProtoMember(1, DataFormat = DataFormat.FixedSize)] public ulong steamid { get; set; }
    [ProtoMember(2)] public uint year { get; set; }
    [ProtoMember(3)] public bool force_regenerate { get; set; }
    [ProtoMember(4)] public int access_source { get; set; }
}

[ProtoContract]
internal sealed class CSaleFeature_GetUserYearInReview_Response : IExtensible
{
    private IExtension? _extensionData;
    IExtension IExtensible.GetExtensionObject(bool createIfMissing) => Extensible.GetExtensionObject(ref _extensionData, createIfMissing);
    [ProtoMember(1)] public YirStats? stats { get; set; }
    [ProtoMember(3)] public YirGlobalPercentiles? percentiles { get; set; }
    [ProtoMember(4)] public YirPlaytimeDistribution? distribution { get; set; }
}

[ProtoContract]
internal sealed class YirStats : IExtensible
{
    private IExtension? _extensionData;
    IExtension IExtensible.GetExtensionObject(bool createIfMissing) => Extensible.GetExtensionObject(ref _extensionData, createIfMissing);
    [ProtoMember(1)] public uint account_id { get; set; }
    [ProtoMember(2)] public uint year { get; set; }
    [ProtoMember(3)] public YirUserPlaytimeStats? playtime_stats { get; set; }
    [ProtoMember(4)] public int privacy_state { get; set; }
}

[ProtoContract]
internal sealed class YirUserPlaytimeStats : IExtensible
{
    private IExtension? _extensionData;
    IExtension IExtensible.GetExtensionObject(bool createIfMissing) => Extensible.GetExtensionObject(ref _extensionData, createIfMissing);
    [ProtoMember(1)] public YirPlaytimeStats? total_stats { get; set; }
    [ProtoMember(2)] public List<YirGamePlaytimeStats> games { get; } = [];
    [ProtoMember(3)] public YirPlaytimeStreak? playtime_streak { get; set; }
    [ProtoMember(5)] public List<YirMonthlyPlaytimeStats> months { get; } = [];
    [ProtoMember(6)] public List<YirGameSummary> game_summary { get; } = [];
    [ProtoMember(7)] public uint demos_played { get; set; }
    [ProtoMember(9)] public uint playtests_played { get; set; }
    [ProtoMember(10)] public YirSummaryStats? summary_stats { get; set; }
    [ProtoMember(11)] public bool substantial { get; set; } = true;
    [ProtoMember(13)] public YirByNumbers? by_numbers { get; set; }
}

[ProtoContract]
internal sealed class YirPlaytimeStats : IExtensible
{
    private IExtension? _extensionData;
    IExtension IExtensible.GetExtensionObject(bool createIfMissing) => Extensible.GetExtensionObject(ref _extensionData, createIfMissing);
    [ProtoMember(1)] public uint total_playtime_seconds { get; set; }
    [ProtoMember(20)] public uint total_sessions { get; set; }
    [ProtoMember(21)] public uint vr_sessions { get; set; }
    [ProtoMember(22)] public uint deck_sessions { get; set; }
    [ProtoMember(23)] public uint controller_sessions { get; set; }
    [ProtoMember(24)] public uint linux_sessions { get; set; }
    [ProtoMember(25)] public uint macos_sessions { get; set; }
    [ProtoMember(26)] public uint windows_sessions { get; set; }
    [ProtoMember(27)] public uint total_playtime_percentagex100 { get; set; }
    [ProtoMember(28)] public uint vr_playtime_percentagex100 { get; set; }
    [ProtoMember(29)] public uint deck_playtime_percentagex100 { get; set; }
    [ProtoMember(30)] public uint controller_playtime_percentagex100 { get; set; }
    [ProtoMember(31)] public uint linux_playtime_percentagex100 { get; set; }
    [ProtoMember(32)] public uint macos_playtime_percentagex100 { get; set; }
    [ProtoMember(33)] public uint windows_playtime_percentagex100 { get; set; }
}

[ProtoContract]
internal sealed class YirGamePlaytimeStats : IExtensible
{
    private IExtension? _extensionData;
    IExtension IExtensible.GetExtensionObject(bool createIfMissing) => Extensible.GetExtensionObject(ref _extensionData, createIfMissing);
    [ProtoMember(1)] public uint appid { get; set; }
    [ProtoMember(2)] public YirPlaytimeStats? stats { get; set; }
    [ProtoMember(3)] public YirPlaytimeStreak? playtime_streak { get; set; }
    [ProtoMember(5)] public uint rtime_first_played { get; set; }
}

[ProtoContract]
internal sealed class YirPlaytimeStreak : IExtensible
{
    private IExtension? _extensionData;
    IExtension IExtensible.GetExtensionObject(bool createIfMissing) => Extensible.GetExtensionObject(ref _extensionData, createIfMissing);
    [ProtoMember(1)] public uint longest_consecutive_days { get; set; }
    [ProtoMember(2)] public uint rtime_start { get; set; }
    [ProtoMember(3)] public List<YirStreakGame> streak_games { get; } = [];
}

[ProtoContract]
internal sealed class YirStreakGame : IExtensible
{
    private IExtension? _extensionData;
    IExtension IExtensible.GetExtensionObject(bool createIfMissing) => Extensible.GetExtensionObject(ref _extensionData, createIfMissing);
    [ProtoMember(1)] public uint appid { get; set; }
}

[ProtoContract]
internal sealed class YirMonthlyPlaytimeStats : IExtensible
{
    private IExtension? _extensionData;
    IExtension IExtensible.GetExtensionObject(bool createIfMissing) => Extensible.GetExtensionObject(ref _extensionData, createIfMissing);
    [ProtoMember(1)] public uint rtime_month { get; set; }
    [ProtoMember(2)] public YirPlaytimeStats? stats { get; set; }
    [ProtoMember(4)] public List<YirGamePlaytimeStats> appid { get; } = [];
    [ProtoMember(6)] public List<YirSimpleGameSummary> game_summary { get; } = [];
}

[ProtoContract]
internal sealed class YirSimpleGameSummary : IExtensible
{
    private IExtension? _extensionData;
    IExtension IExtensible.GetExtensionObject(bool createIfMissing) => Extensible.GetExtensionObject(ref _extensionData, createIfMissing);
    [ProtoMember(1)] public uint appid { get; set; }
    [ProtoMember(2)] public uint total_playtime_percentagex100 { get; set; }
    [ProtoMember(3)] public uint relative_playtime_percentagex100 { get; set; }
}

[ProtoContract]
internal sealed class YirGameSummary : IExtensible
{
    private IExtension? _extensionData;
    IExtension IExtensible.GetExtensionObject(bool createIfMissing) => Extensible.GetExtensionObject(ref _extensionData, createIfMissing);
    [ProtoMember(1)] public uint appid { get; set; }
    [ProtoMember(2)] public bool new_this_year { get; set; }
    [ProtoMember(3)] public uint rtime_first_played_lifetime { get; set; }
    [ProtoMember(4)] public bool demo { get; set; }
    [ProtoMember(5)] public bool playtest { get; set; }
    [ProtoMember(13)] public uint total_playtime_percentagex100 { get; set; }
    [ProtoMember(14)] public uint total_sessions { get; set; }
}

[ProtoContract]
internal sealed class YirSummaryStats : IExtensible
{
    private IExtension? _extensionData;
    IExtension IExtensible.GetExtensionObject(bool createIfMissing) => Extensible.GetExtensionObject(ref _extensionData, createIfMissing);
    [ProtoMember(2)] public uint total_achievements { get; set; }
    [ProtoMember(3)] public uint total_games_with_achievements { get; set; }
    [ProtoMember(4)] public uint total_rare_achievements { get; set; }
}

[ProtoContract]
internal sealed class YirByNumbers : IExtensible
{
    private IExtension? _extensionData;
    IExtension IExtensible.GetExtensionObject(bool createIfMissing) => Extensible.GetExtensionObject(ref _extensionData, createIfMissing);
    [ProtoMember(13)] public uint games_played_pct { get; set; }
    [ProtoMember(14)] public uint achievements_pct { get; set; }
    [ProtoMember(15)] public uint game_streak_pct { get; set; }
    [ProtoMember(16)] public uint games_played_avg { get; set; }
    [ProtoMember(17)] public uint achievements_avg { get; set; }
    [ProtoMember(18)] public uint game_streak_avg { get; set; }
}

[ProtoContract]
internal sealed class YirGlobalPercentiles : IExtensible
{
    private IExtension? _extensionData;
    IExtension IExtensible.GetExtensionObject(bool createIfMissing) => Extensible.GetExtensionObject(ref _extensionData, createIfMissing);
    [ProtoMember(1)] public uint median_achievements { get; set; }
    [ProtoMember(2)] public uint median_games { get; set; }
    [ProtoMember(3)] public uint median_streak { get; set; }
}

[ProtoContract]
internal sealed class YirPlaytimeDistribution : IExtensible
{
    private IExtension? _extensionData;
    IExtension IExtensible.GetExtensionObject(bool createIfMissing) => Extensible.GetExtensionObject(ref _extensionData, createIfMissing);
    [ProtoMember(1)] public uint new_releases { get; set; }
    [ProtoMember(2)] public uint recent_releases { get; set; }
    [ProtoMember(3)] public uint classic_releases { get; set; }
    [ProtoMember(4)] public uint recent_cutoff_year { get; set; }
}

internal sealed class SaleFeatureUnifiedService : SteamUnifiedMessages.UnifiedService
{
    public override string ServiceName => YearInReviewProtocol.Service;

    public override void HandleResponseMsg(string methodName, PacketClientMsgProtobuf packetMsg)
    {
        if (methodName == YearInReviewProtocol.GetUserYearInReviewMethod)
            PostResponseMsg<CSaleFeature_GetUserYearInReview_Response>(packetMsg);
    }

    public override void HandleNotificationMsg(string methodName, PacketClientMsgProtobuf packetMsg)
    {
    }
}
