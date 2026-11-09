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
    uint Order,
    int? TotalAchievements = null,
    int? CompletionAchievements = null);

/// <summary>
/// 概览进度。Total / Unlocked 的含义由 Steam 决定（成就集上线后是否按玩家计算还没用真实账号确认），
/// 详情的口径见 <see cref="SteamAchievementSummary"/>。后五个字段来自成就集新增的协议字段，旧缓存里是 null。
/// </summary>
public sealed record SteamAchievementAppProgressSnapshot(
    uint AppId,
    int Total,
    int Unlocked,
    double Percentage,
    int? BaseGameTotal = null,
    int? GroupsCompletedBase = null,
    int? GroupsTotalBase = null,
    int? GroupsCompletedDlc = null,
    int? GroupsTotalDlc = null);

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
    /// <summary>成就本身或所在的集已归档。</summary>
    public bool IsArchived { get; init; } = Definition.Archived;

    /// <summary>是否计入完成度：所在的集计入，且不是未获得的归档成就。</summary>
    public bool IsCounted { get; init; } = true;

    /// <summary>未获得的归档成就和隐藏成就一样不展示。</summary>
    public bool IsRevealed => IsUnlocked == true || (!Definition.Hidden && !IsArchived);
}

/// <summary>
/// 一个成就集对这名玩家的进度。Total 不含未获得的归档成就，AllTotal 是集内全部成就。
/// Completed 可能在 Unlocked 小于 Total 时仍为 true：集完成后再加成就，完成状态保留。
/// </summary>
public sealed record SteamAchievementSet(
    uint GroupId,
    string LocalizedName,
    uint? DlcAppId,
    bool Archived,
    bool Counted,
    bool? Owned,
    int Total,
    int Unlocked,
    int AllTotal,
    bool Completed,
    DateTimeOffset? CompletedAt);

/// <summary>
/// Total / Unlocked 是 Steam 口径：基础集加已拥有的 DLC 集，未获得的归档成就不计。
/// AllTotal / AllUnlocked 是全部成就，作为次要数字显示。
/// </summary>
public sealed record SteamAchievementSummary(
    int Total,
    int Unlocked,
    int Unknown,
    double? Percentage,
    int AllTotal = 0,
    int AllUnlocked = 0);

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
    public IReadOnlyList<SteamAchievementSet> Sets { get; init; } = [];

    /// <summary>DLC 是否按授权数据精确判断；否则用「解锁过该集任一成就才计入」推断。</summary>
    public bool OwnershipKnown { get; init; }
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
