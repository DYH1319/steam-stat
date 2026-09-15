using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Core.Features.Achievements;

public enum SteamAchievementProgressType
{
    None,
    Int,
    Float,
    Unknown
}

public sealed record SteamAchievementSchemaSnapshot(
    uint AppId,
    string Language,
    int ValveSchemaVersion,
    uint SchemaHash,
    IReadOnlyList<SteamAchievementDefinition> Definitions,
    IReadOnlyList<SteamAchievementGroup> Groups);

public sealed record SteamAchievementDefinition(
    uint? InternalKey,
    string InternalName,
    string LocalizedName,
    string LocalizedDescription,
    string Icon,
    string IconGray,
    bool Hidden,
    double? GlobalUnlockedPercent,
    uint? GroupId,
    bool Archived,
    SteamAchievementProgressType ProgressType,
    double? MinProgress,
    double? MaxProgress);

public sealed record SteamAchievementGroup(
    uint GroupId,
    string LocalizedName,
    uint? DlcAppId,
    bool Archived,
    bool DeveloperOnly,
    bool IsPublic,
    uint Order);

public sealed record SteamAchievementAppProgressSnapshot(
    uint AppId,
    int Total,
    int Unlocked,
    double Percentage);

public sealed record SteamAchievementUnlock(
    uint? InternalKey,
    string InternalName,
    bool? IsUnlocked,
    DateTimeOffset? UnlockTimeUtc);

public sealed record SteamAchievementUnlockSnapshot(
    ulong SteamId,
    uint AppId,
    long SessionGeneration,
    IReadOnlyList<SteamAchievementUnlock> Entries);

public sealed record SteamAchievementResourceState(
    SteamDataSource? Source,
    SteamFreshness? Freshness,
    DateTimeOffset? LastSuccessfulUpdate,
    SteamFailureKind? Failure,
    string? DiagnosticCode)
{
    public bool HasValue => Source != null;

    public static SteamAchievementResourceState From<T>(SteamGatewayResult<T> result)
        => new(result.Source, result.Freshness, result.FetchedAt, result.Failure, result.DiagnosticCode);
}

public sealed record SteamAchievementEntry(
    SteamAchievementDefinition Definition,
    bool? IsUnlocked,
    DateTimeOffset? UnlockTimeUtc)
{
    public bool IsRevealed => !Definition.Hidden || IsUnlocked == true;
}

public sealed record SteamAchievementSummary(
    int Total,
    int Unlocked,
    int Unknown,
    double? Percentage);

public sealed record SteamAchievementGameResult(
    uint AppId,
    string Language,
    SteamAchievementSchemaSnapshot? Schema,
    IReadOnlyList<SteamAchievementEntry> Achievements,
    SteamAchievementSummary Summary,
    SteamAchievementResourceState SchemaState,
    SteamAchievementResourceState ProgressState,
    IReadOnlyList<string> UnmatchedUnlockNames)
{
    public bool IsSuccess => Schema != null;
    public bool IsEmpty => Schema != null && Schema.Definitions.Count == 0;
    public bool IsStale => IsSuccess && SchemaState.Freshness is SteamFreshness.Stale or SteamFreshness.Expired;
    public bool IsPartial => IsSuccess && !ProgressState.HasValue;
    public uint? SchemaHash => Schema?.SchemaHash;
    public string AppName { get; init; } = string.Empty;
}

public sealed record SteamAchievementOverviewItem(
    uint AppId,
    string Name,
    string LocalizedName,
    int PlaytimeForever,
    long LastPlayedAt,
    SteamAchievementAppProgressSnapshot? Progress);

public sealed record SteamAchievementOverviewResult(
    string AccountName,
    IReadOnlyList<SteamAchievementOverviewItem> Games,
    SteamAchievementResourceState ProgressState)
{
    public bool IsPartial => ProgressState.DiagnosticCode == SteamAchievementDiagnosticCodes.ProgressPartial;
}
