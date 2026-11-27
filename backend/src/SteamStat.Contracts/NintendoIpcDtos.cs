namespace SteamStat.Contracts.Ipc;

// ---------------------------------------------------------------------------
// Nintendo 游玩记录（实验功能）
//
// 数据来自任天堂没有公开的 Play Activity 接口：每款游戏的累计分钟数和最近若干天的日级分钟数。
// 时长合计一律以日级数据（official）为准；「大约几点在玩」是用两次读取之间的增量推算的（inferred），只用于时段分布。
// 日期一律是「距 1970-01-01 的天数」（任天堂那边的日期，不跟随应用的逻辑日分界点），时间是 Unix 秒。
// ---------------------------------------------------------------------------

public sealed record NintendoAccountDto
{
    /// <summary>任天堂账号 ID（16 位十六进制）。</summary>
    public required string NaId { get; init; }
    public required string Nickname { get; init; }
    [IpcOptional] public string? IconUrl { get; init; }
    [IpcOptional] public string? Country { get; init; }
    /// <summary>属于用户本人。全平台汇总和周刊只算这些账号。</summary>
    public required bool Mine { get; init; }
    [IpcNumber] public required long LinkedAt { get; init; }
    [IpcOptional, IpcNumber] public long? LastSyncAt { get; init; }
    [IpcOptional, IpcNumber] public long? LastSuccessAt { get; init; }
    /// <summary>
    /// ok：正常；signedOut：本机没有可用的登录（换了设备、令牌过期或被撤销）；network：连不上；
    /// layout：任天堂的回复和预期不符（接口可能改了）；failed：其他失败；pending：还没读过。
    /// </summary>
    [IpcStringValues("ok", "signedOut", "network", "layout", "failed", "pending")] public required string State { get; init; }
}

public sealed record NintendoStatusDto
{
    /// <summary>设置里打开了并且确认过风险说明。</summary>
    public required bool Active { get; init; }
    /// <summary>能把登录安全地保存到磁盘（Linux 需要密钥环）。为假时登录只在本次运行有效。</summary>
    public required bool CanPersist { get; init; }
    public required bool Syncing { get; init; }
    public required IReadOnlyList<NintendoAccountDto> Accounts { get; init; }
}

public sealed record NintendoLoginBeginDto
{
    public required string LoginId { get; init; }
    /// <summary>登录地址，登录窗口打不开或截不到回调时让用户在浏览器里打开。</summary>
    public required string Url { get; init; }
    /// <summary>应用内登录窗口是否已经打开。</summary>
    public required bool WindowOpened { get; init; }
}

public sealed record NintendoLoginCompleteRequest
{
    [IpcMaxLength(64)] public required string LoginId { get; init; }
    /// <summary>用户复制的「选择此人」链接（npf…://auth#session_token_code=…）。</summary>
    [IpcMaxLength(8192)] public required string CallbackUrl { get; init; }
}

public sealed record NintendoLoginIdRequest
{
    [IpcMaxLength(64)] public required string LoginId { get; init; }
}

public sealed record NintendoLoginResultDto
{
    public required string LoginId { get; init; }
    [IpcStringValues("linked", "relinked", "cancelled", "notCallback", "stateMismatch", "denied", "expired", "network", "failed", "inactive")]
    public required string Status { get; init; }
    [IpcOptional] public NintendoAccountDto? Account { get; init; }
}

public sealed record NintendoAccountUpdateRequest
{
    [IpcMaxLength(32)] public required string NaId { get; init; }
    [IpcOptional] public bool? Mine { get; init; }
}

public sealed record NintendoAccountUnlinkRequest
{
    [IpcMaxLength(32)] public required string NaId { get; init; }
    /// <summary>同时删除这个账号的时长数据。否则只删除登录，数据留在档案里。</summary>
    public required bool DeleteData { get; init; }
}

public sealed record NintendoScopeRequest
{
    /// <summary>空表示全部账号。</summary>
    [IpcOptional, IpcMaxLength(32)] public IReadOnlyList<string>? NaIds { get; init; }
}

public sealed record NintendoSyncResultDto
{
    [IpcStringValues("ok", "partial", "failed", "inactive", "noAccounts")] public required string Status { get; init; }
    public required int Accounts { get; init; }
    public required int Titles { get; init; }
    public required int Days { get; init; }
}

public sealed record NintendoTitleDto
{
    public required string TitleId { get; init; }
    [IpcStringValues("switch", "switch2", "other")] public required string Platform { get; init; }
    public required string Name { get; init; }
    [IpcOptional] public string? ImageUrl { get; init; }
}

/// <summary>官方累计值。Title 是 Titles 里的下标，Account 是 Accounts 里的下标。</summary>
public sealed record NintendoTotalDto
{
    public required int Title { get; init; }
    public required int Account { get; init; }
    public required int TotalMinutes { get; init; }
    public required int TotalDays { get; init; }
    [IpcOptional, IpcNumber] public long? FirstPlayedAt { get; init; }
    [IpcOptional, IpcNumber] public long? LastPlayedAt { get; init; }
}

/// <summary>
/// 渲染层用的紧凑数据集。Daily 每 4 个数一条：标题下标、账号下标、日（距 1970-01-01 的天数）、分钟；
/// Intervals 每 5 个数一条：标题下标、账号下标、开始、结束（Unix 秒）、分钟（推算）。
/// </summary>
public sealed record NintendoDatasetDto
{
    public required IReadOnlyList<string> Accounts { get; init; }
    public required IReadOnlyList<NintendoTitleDto> Titles { get; init; }
    public required IReadOnlyList<NintendoTotalDto> Totals { get; init; }
    public required IReadOnlyList<int> Daily { get; init; }
    public required IReadOnlyList<int> Intervals { get; init; }
    /// <summary>推算区间里能放到具体时间上的分钟数和放不上的分钟数，用来算时段覆盖率。</summary>
    public required int PlacedMinutes { get; init; }
    public required int UnplacedMinutes { get; init; }
}

public sealed record NintendoRevisionDto
{
    public required string Revision { get; init; }
}

public sealed record NintendoTitleHistoryRequest
{
    [IpcMaxLength(32)] public required string TitleId { get; init; }
    [IpcOptional, IpcMaxLength(32)] public IReadOnlyList<string>? NaIds { get; init; }
}

/// <summary>累计值的一段平台期：在这段时间里读到的累计分钟数都是 Minutes。</summary>
public sealed record NintendoSegmentDto
{
    public required string NaId { get; init; }
    [IpcStringValues("switch", "switch2", "other")] public required string Platform { get; init; }
    public required int Minutes { get; init; }
    [IpcNumber] public required long FirstObservedAt { get; init; }
    [IpcNumber] public required long LastObservedAt { get; init; }
}

public sealed record NintendoSettingsDto
{
    public required bool Enabled { get; init; }
    public required bool RiskAcknowledged { get; init; }
    public required int PollMinutes { get; init; }
    public required IReadOnlyList<string> HiddenTitleIds { get; init; }
}

public sealed record NintendoSettingsPatchDto
{
    [IpcOptional] public bool? Enabled { get; init; }
    [IpcOptional] public bool? RiskAcknowledged { get; init; }
    [IpcOptional, IpcRange(5, 240)] public int? PollMinutes { get; init; }
    [IpcOptional, IpcMaxLength(32)] public IReadOnlyList<string>? HiddenTitleIds { get; init; }
}
