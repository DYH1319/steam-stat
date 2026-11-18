namespace SteamStat.Contracts.Ipc;

// ---------------------------------------------------------------------------
// 好友与圈子：一起玩的严格判定、友情时间线、契合度、开黑助手、圈子排行榜、入坑传播、
// 好友入库档案、成就竞速、交换卡、双人回顾。
//
// 渲染层传来的用户文字（交换卡的昵称、口令、粘贴的卡）编码成「s1:」再传，桥接层不会改写。
// 168 格的周作息：下标 = 星期 × 24 + 小时，星期按 WeekStart 对齐；没有数据的格子是 -1。
// ---------------------------------------------------------------------------

public sealed record CircleAppNameDto
{
    public required int AppId { get; init; }
    public required string Name { get; init; }
}

public sealed record CircleFriendRequest
{
    public required string FriendSteamId { get; init; }
    [IpcOptional] public IReadOnlyList<string>? SteamIds { get; init; }
}

public sealed record CircleFilterRequest
{
    [IpcOptional] public string? FriendSteamId { get; init; }
    [IpcOptional] public IReadOnlyList<string>? SteamIds { get; init; }
}

// --- 一起玩 ---------------------------------------------------------------

public sealed record CircleTogetherSegmentDto
{
    [IpcNumber] public required long SessionId { get; init; }
    public required string FriendSteamId { get; init; }
    public required int AppId { get; init; }
    public required string Name { get; init; }
    [IpcNumber] public required long Start { get; init; }
    [IpcNumber] public required long End { get; init; }
    [IpcNumber] public required long Seconds { get; init; }
    /// <summary>其中确认在同一个大厅、服务器或 Rich Presence 组里的秒数。</summary>
    [IpcNumber] public required long ConfirmedSeconds { get; init; }
}

public sealed record CircleTogetherDto
{
    public required IReadOnlyList<CircleTogetherSegmentDto> Segments { get; init; }
    /// <summary>第一次记下这位好友的分组信息的时间，之前的记录只能按「同时在玩」判断。</summary>
    [IpcOptional, IpcNumber] public long? GroupDataSince { get; init; }
}

// --- 友情时间线 ------------------------------------------------------------

public sealed record FriendBondYearDto
{
    public required int Year { get; init; }
    [IpcNumber] public required long Seconds { get; init; }
}

public sealed record FriendBondDto
{
    public required string SteamId { get; init; }
    public required string Name { get; init; }
    [IpcOptional] public string? AvatarHash { get; init; }
    public required bool IsFriend { get; init; }
    public required bool Tracked { get; init; }
    [IpcOptional, IpcNumber] public long? Since { get; init; }
    [IpcStringValues("official", "recorded", "unknown")] public required string SinceEvidence { get; init; }
    [IpcOptional, IpcNumber] public long? RemovedAt { get; init; }
    [IpcNumber] public required long TogetherSeconds { get; init; }
    [IpcNumber] public required long ConfirmedSeconds { get; init; }
    public required int TogetherCount { get; init; }
    [IpcOptional, IpcNumber] public long? FirstTogetherAt { get; init; }
    [IpcOptional] public int? FirstTogetherAppId { get; init; }
    public required bool FirstTogetherConfirmed { get; init; }
    [IpcOptional, IpcNumber] public long? LastTogether { get; init; }
    public required IReadOnlyList<FriendBondYearDto> Years { get; init; }
    [IpcOptional] public int? PeakYear { get; init; }
    [IpcOptional, IpcNumber] public long? NextAnniversary { get; init; }
    [IpcOptional] public int? NextAnniversaryYears { get; init; }
    [IpcNumber] public required long LastLogOff { get; init; }
    [IpcStringValues("active", "lost", "dormant", "removed", "new", "occasional", "quiet")] public required string Status { get; init; }
    public required IReadOnlyList<int> TopApps { get; init; }
}

public sealed record FriendshipEventDto
{
    [IpcStringValues("met", "added", "removed", "firstTogether", "peak", "anniversary", "lost")] public required string Kind { get; init; }
    public required string SteamId { get; init; }
    public required string Name { get; init; }
    [IpcNumber] public required long Time { get; init; }
    [IpcOptional] public int? AppId { get; init; }
    [IpcOptional, IpcNumber] public long? Value { get; init; }
    public required bool Confirmed { get; init; }
    [IpcStringValues("official", "recorded", "unknown")] public required string Evidence { get; init; }
}

public sealed record FriendshipDto
{
    public required IReadOnlyList<FriendBondDto> Bonds { get; init; }
    public required IReadOnlyList<FriendshipEventDto> Events { get; init; }
    public required IReadOnlyList<CircleAppNameDto> Names { get; init; }
    /// <summary>读到过 Steam 的成为好友时间。没读到时只有 Steam Stat 记录下来的加好友时间。</summary>
    public required bool OfficialSince { get; init; }
    [IpcOptional, IpcNumber] public long? RecordsSince { get; init; }
}

// --- 契合度 -----------------------------------------------------------------

public sealed record CompatibilityDto
{
    public required string SteamId { get; init; }
    public required string Name { get; init; }
    [IpcOptional] public string? AvatarHash { get; init; }
    [IpcOptional] public double? Overall { get; init; }
    [IpcOptional] public double? Schedule { get; init; }
    [IpcOptional] public double? Taste { get; init; }
    [IpcOptional] public double? Together { get; init; }
    public required double MyHours { get; init; }
    public required double TheirHours { get; init; }
    public required int MyGames { get; init; }
    public required int TheirGames { get; init; }
    public required int SharedGames { get; init; }
    public required IReadOnlyList<int> TopShared { get; init; }
    [IpcOptional] public int? BestCell { get; init; }
    [IpcNumber] public required long TogetherSeconds { get; init; }
}

public sealed record CompatibilityReportDto
{
    public required IReadOnlyList<CompatibilityDto> Friends { get; init; }
    public required IReadOnlyList<CircleAppNameDto> Names { get; init; }
    public required int WeekStart { get; init; }
}

// --- 开黑助手 ----------------------------------------------------------------

public sealed record PartyPlanRequest
{
    public required IReadOnlyList<string> FriendSteamIds { get; init; }
    [IpcOptional] public IReadOnlyList<string>? SteamIds { get; init; }
}

public sealed record PartyMemberDto
{
    public required string SteamId { get; init; }
    public required string Name { get; init; }
    [IpcOptional] public string? AvatarHash { get; init; }
    public required bool IsMe { get; init; }
    [IpcStringValues("ok", "private", "failed", "signedOut")] public required string Library { get; init; }
    public required int Games { get; init; }
    public required bool HasForecast { get; init; }
    public required bool Tracked { get; init; }
}

public sealed record PartyMinutesDto
{
    public required string SteamId { get; init; }
    public required int Minutes { get; init; }
}

public sealed record PartyGameDto
{
    public required int AppId { get; init; }
    public required string Name { get; init; }
    public required bool Verified { get; init; }
    public required IReadOnlyList<string> Owners { get; init; }
    public required IReadOnlyList<string> Missing { get; init; }
    public required IReadOnlyList<PartyMinutesDto> Minutes { get; init; }
    public required int RecentPlayers { get; init; }
}

public sealed record PartySlotDto
{
    public required int Weekday { get; init; }
    public required int StartHour { get; init; }
    public required int EndHour { get; init; }
    public required double Probability { get; init; }
    public required double Expected { get; init; }
}

public sealed record PartyPlanDto
{
    public required IReadOnlyList<PartyMemberDto> Members { get; init; }
    public required IReadOnlyList<PartyGameDto> Common { get; init; }
    public required IReadOnlyList<PartyGameDto> NearMiss { get; init; }
    public required IReadOnlyList<PartySlotDto> Slots { get; init; }
    /// <summary>168 格，所有人都在线的概率，-1 表示有人没有这一格的数据。</summary>
    public required IReadOnlyList<double> Together { get; init; }
    /// <summary>168 格，预计在线的好友人数。</summary>
    public required IReadOnlyList<double> Expected { get; init; }
    public required int WeekStart { get; init; }
    public required int Weeks { get; init; }
}

// --- 排行榜 -----------------------------------------------------------------

public sealed record LeaderboardRequest
{
    [IpcOptional] public int? AppId { get; init; }
    [IpcOptional] public IReadOnlyList<string>? SteamIds { get; init; }
}

public sealed record BoardEntryDto
{
    public required string SteamId { get; init; }
    public required string Name { get; init; }
    [IpcOptional] public string? AvatarHash { get; init; }
    public required bool IsMe { get; init; }
    [IpcStringValues("me", "snapshot", "card")] public required string Source { get; init; }
    [IpcNumber] public required long AsOf { get; init; }
    [IpcOptional] public int? Last7 { get; init; }
    [IpcOptional] public int? Last30 { get; init; }
    [IpcOptional] public int? Lifetime { get; init; }
    [IpcOptional] public int? Games { get; init; }
    [IpcOptional] public int? Achievements { get; init; }
    [IpcOptional, IpcNumber] public long? ObservedSince { get; init; }
}

public sealed record GameBoardEntryDto
{
    public required string SteamId { get; init; }
    public required string Name { get; init; }
    [IpcOptional] public string? AvatarHash { get; init; }
    public required bool IsMe { get; init; }
    [IpcStringValues("me", "snapshot", "card")] public required string Source { get; init; }
    [IpcNumber] public required long AsOf { get; init; }
    public required int Minutes { get; init; }
    [IpcOptional] public int? Last30 { get; init; }
}

public sealed record LeaderboardDto
{
    public required IReadOnlyList<BoardEntryDto> Entries { get; init; }
    /// <summary>可以选的游戏：至少两个人有时长的。</summary>
    public required IReadOnlyList<CircleAppNameDto> Games { get; init; }
    [IpcOptional] public int? AppId { get; init; }
    public required IReadOnlyList<GameBoardEntryDto> Game { get; init; }
    public required int TrackedFriends { get; init; }
    public required int Cards { get; init; }
}

// --- 入坑传播 ----------------------------------------------------------------

public sealed record AdoptionRequest
{
    [IpcOptional] public int? AppId { get; init; }
    [IpcOptional] public IReadOnlyList<string>? SteamIds { get; init; }
}

public sealed record AdopterDto
{
    public required string SteamId { get; init; }
    public required string Name { get; init; }
    [IpcOptional] public string? AvatarHash { get; init; }
    public required bool IsMe { get; init; }
    [IpcNumber] public required long Time { get; init; }
    [IpcStringValues("recorded", "inferred", "before")] public required string Evidence { get; init; }
    [IpcOptional] public string? InfluencedBy { get; init; }
    [IpcNumber] public required long InfluenceSeconds { get; init; }
}

public sealed record AdoptionChainDto
{
    public required int AppId { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<AdopterDto> Adopters { get; init; }
    [IpcOptional] public int? MyRank { get; init; }
}

public sealed record AdoptionDto
{
    public required IReadOnlyList<AdoptionChainDto> Chains { get; init; }
    [IpcOptional] public AdoptionChainDto? Selected { get; init; }
    public required int TrackedFriends { get; init; }
}

// --- 好友入库档案 ------------------------------------------------------------

public sealed record FriendLibraryEventDto
{
    public required string SteamId { get; init; }
    public required string Name { get; init; }
    public required int AppId { get; init; }
    public required string AppName { get; init; }
    [IpcStringValues("added", "removed")] public required string Kind { get; init; }
    [IpcNumber] public required long Time { get; init; }
    /// <summary>我也有这款游戏。</summary>
    public required bool Owned { get; init; }
    [IpcOptional] public int? Minutes { get; init; }
}

public sealed record FriendLibraryDto
{
    public required IReadOnlyList<FriendLibraryEventDto> Events { get; init; }
    /// <summary>第一次采集的时间：这之前的游戏只是基线，不算入库。</summary>
    [IpcOptional, IpcNumber] public long? BaselineAt { get; init; }
    public required int BaselineGames { get; init; }
    public required int TrackedFriends { get; init; }
}

// --- 成就竞速 ----------------------------------------------------------------

public sealed record AchievementRaceRowDto
{
    public required int AppId { get; init; }
    public required string Name { get; init; }
    public required int MyUnlocked { get; init; }
    public required int MyTotal { get; init; }
    public required int TheirUnlocked { get; init; }
    public required int TheirTotal { get; init; }
    [IpcStringValues("me", "them", "tie")] public required string Leader { get; init; }
    /// <summary>对方最近 30 天解锁了几个（来自进度快照的差值）。</summary>
    public required int TheirRecent { get; init; }
    public required int MyRecent { get; init; }
    [IpcOptional, IpcNumber] public long? TheirChangedAt { get; init; }
    /// <summary>最近一次被对方反超的时间。</summary>
    [IpcOptional, IpcNumber] public long? OvertakenAt { get; init; }
}

public sealed record AchievementRaceDto
{
    public required IReadOnlyList<AchievementRaceRowDto> Rows { get; init; }
    public required int MyLeads { get; init; }
    public required int TheirLeads { get; init; }
    public required int Ties { get; init; }
    [IpcOptional, IpcNumber] public long? ObservedSince { get; init; }
}

// --- 交换卡 -----------------------------------------------------------------

public sealed record ExchangeCardExportRequest
{
    public required int Year { get; init; }
    /// <summary>卡上显示的名字，「s1:」编码。空时用 Steam 昵称。</summary>
    [IpcOptional, IpcMaxLength(512)] public string? Name { get; init; }
    /// <summary>口令，「s1:」编码。空时卡只是打包，不保密。</summary>
    [IpcOptional, IpcMaxLength(1024)] public string? Passphrase { get; init; }
    [IpcOptional] public IReadOnlyList<string>? SteamIds { get; init; }
}

public sealed record ExchangeCardImportRequest
{
    /// <summary>粘贴的文字，「s1:」编码。里面带着卡就行，不必只有卡。</summary>
    [IpcMaxLength(400_000)] public required string Text { get; init; }
    [IpcOptional, IpcMaxLength(1024)] public string? Passphrase { get; init; }
}

public sealed record ExchangeCardSaveRequest
{
    [IpcMaxLength(80_000)] public required string Code { get; init; }
    public required int Year { get; init; }
}

public sealed record ExchangeCardIdRequest
{
    public required int Id { get; init; }
}

public sealed record ExchangeCardGameDto
{
    public required int AppId { get; init; }
    public required string Name { get; init; }
    public required int Minutes { get; init; }
    public required int YearMinutes { get; init; }
    public required int Unlocked { get; init; }
    public required int Total { get; init; }
    [IpcNumber] public required long LastPlayed { get; init; }
}

public sealed record ExchangeCardDto
{
    [IpcOptional] public int? Id { get; init; }
    public required string SteamId { get; init; }
    public required string Name { get; init; }
    public required int Year { get; init; }
    [IpcNumber] public required long GeneratedAt { get; init; }
    [IpcOptional, IpcNumber] public long? ImportedAt { get; init; }
    public required int UtcOffsetMinutes { get; init; }
    public required int LifetimeMinutes { get; init; }
    public required int YearMinutes { get; init; }
    public required int Last7Minutes { get; init; }
    public required int Last30Minutes { get; init; }
    public required int OwnedGames { get; init; }
    public required int PlayedGames { get; init; }
    public required int CompletedGames { get; init; }
    public required int Achievements { get; init; }
    public required int YearAchievements { get; init; }
    public required IReadOnlyList<int> Months { get; init; }
    /// <summary>168 格，星期从周一开始，0..255。</summary>
    public required IReadOnlyList<int> Heatmap { get; init; }
    public required IReadOnlyList<ExchangeCardGameDto> Games { get; init; }
    [IpcOptional] public string? PersonaCode { get; init; }
    [IpcOptional] public string? PersonaZh { get; init; }
    [IpcOptional] public string? PersonaEn { get; init; }
    /// <summary>发卡人现在是不是我的好友（只对导入的卡有意义）。</summary>
    public required bool IsFriend { get; init; }
}

public sealed record ExchangeCardExportDto
{
    public required string Code { get; init; }
    public required bool Protected { get; init; }
    public required ExchangeCardDto Card { get; init; }
}

public sealed record ExchangeCardImportResultDto
{
    [IpcStringValues("ok", "needsPassphrase", "wrongPassphrase", "invalid", "unsupported", "self")] public required string Status { get; init; }
    [IpcOptional] public ExchangeCardDto? Card { get; init; }
    /// <summary>同一个人更早的卡还在，排行榜用最新的一张。</summary>
    public required bool Updated { get; init; }
}

public sealed record ExchangeCardFileDto
{
    [IpcOptional] public string? Text { get; init; }
    public required bool Cancelled { get; init; }
}

// --- 双人回顾 ----------------------------------------------------------------

public sealed record DuoReviewRequest
{
    public required string FriendSteamId { get; init; }
    public required int Year { get; init; }
    [IpcOptional] public IReadOnlyList<string>? SteamIds { get; init; }
}

public sealed record DuoGameDto
{
    public required int AppId { get; init; }
    public required string Name { get; init; }
    public required int Mine { get; init; }
    public required int Theirs { get; init; }
}

public sealed record DuoTogetherGameDto
{
    public required int AppId { get; init; }
    public required string Name { get; init; }
    [IpcNumber] public required long Seconds { get; init; }
    [IpcNumber] public required long ConfirmedSeconds { get; init; }
}

public sealed record DuoReviewDto
{
    public required int Year { get; init; }
    public required int TheirYear { get; init; }
    [IpcNumber] public required long TheirGeneratedAt { get; init; }
    public required string MyName { get; init; }
    public required string TheirName { get; init; }
    public required string TheirSteamId { get; init; }
    public required int MyMinutes { get; init; }
    public required int TheirMinutes { get; init; }
    public required IReadOnlyList<int> MyMonths { get; init; }
    public required IReadOnlyList<int> TheirMonths { get; init; }
    public required IReadOnlyList<DuoGameDto> Shared { get; init; }
    public required IReadOnlyList<DuoGameDto> OnlyMine { get; init; }
    public required IReadOnlyList<DuoGameDto> OnlyTheirs { get; init; }
    [IpcOptional] public double? Schedule { get; init; }
    [IpcOptional] public double? Taste { get; init; }
    /// <summary>星期从周一开始 × 24 + 小时，我的本地时间。</summary>
    [IpcOptional] public int? BestCell { get; init; }
    [IpcOptional] public int? MyPeakCell { get; init; }
    [IpcOptional] public int? TheirPeakCell { get; init; }
    [IpcOptional] public int? BusiestMonth { get; init; }
    [IpcNumber] public required long TogetherSeconds { get; init; }
    [IpcNumber] public required long ConfirmedSeconds { get; init; }
    [IpcOptional, IpcNumber] public long? FirstTogether { get; init; }
    public required IReadOnlyList<DuoTogetherGameDto> TogetherGames { get; init; }
    public required int MyAchievements { get; init; }
    public required int TheirAchievements { get; init; }
    [IpcOptional] public string? MyPersona { get; init; }
    [IpcOptional] public string? TheirPersona { get; init; }
    public required IReadOnlyList<int> SameAxes { get; init; }
    [IpcOptional, IpcNumber] public long? Since { get; init; }
    /// <summary>我和对方的卡都有的 168 格作息（对方已挪到我的时区），用来画两条叠在一起的分布。</summary>
    public required IReadOnlyList<int> MyHeatmap { get; init; }
    public required IReadOnlyList<int> TheirHeatmap { get; init; }
}
