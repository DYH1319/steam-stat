namespace SteamStat.Contracts.Ipc;

/// <summary>按账号过滤。空表示全部账号，和账号切换器的语义一样。</summary>
public sealed record ArchiveScopeRequest
{
    [IpcOptional] public IReadOnlyList<string>? SteamIds { get; init; }
}

/// <summary>一项档案采集的运行状态。</summary>
public sealed record ArchiveCollectorStatusDto
{
    public required string Id { get; init; }
    public required double IntervalHours { get; init; }
    [IpcOptional, IpcNumber] public long? LastRunAt { get; init; }
    [IpcOptional, IpcNumber] public long? LastSuccessAt { get; init; }
    /// <summary>signedOut / layout / network / private / read，成功时为空。</summary>
    [IpcOptional] public string? LastError { get; init; }
    [IpcOptional] public int? LastItems { get; init; }
}

public sealed record ArchiveCollectorRunRequest
{
    [IpcMaxLength(32)] public required string Id { get; init; }
}

public sealed record WebCaptureSummaryDto
{
    [IpcStringValues("billing", "reviews", "guides", "favorites", "comments")] public required string Kind { get; init; }
    public required int Captures { get; init; }
    [IpcNumber] public required long Bytes { get; init; }
    [IpcOptional, IpcNumber] public long? LastCapturedAt { get; init; }
    /// <summary>最近一次是否有页面解析失败。原始页面还在，可以重新解析。</summary>
    public required int LayoutFailures { get; init; }
}

// ---------------------------------------------------------------------------
// 单游戏档案
// ---------------------------------------------------------------------------

public sealed record GameArchiveRequest
{
    [IpcRange(1, int.MaxValue)] public required int AppId { get; init; }
    [IpcOptional] public IReadOnlyList<string>? SteamIds { get; init; }
}

public sealed record PlatformPlaytimeDto
{
    public required string SteamId { get; init; }
    public required int AppId { get; init; }
    public required int WindowsMinutes { get; init; }
    public required int MacMinutes { get; init; }
    public required int LinuxMinutes { get; init; }
    public required int DeckMinutes { get; init; }
    public required int DisconnectedMinutes { get; init; }
    [IpcOptional, IpcNumber] public long? FirstPlayed { get; init; }
    [IpcOptional, IpcNumber] public long? LastPlayed { get; init; }
    [IpcOptional, IpcNumber] public long? FirstWindows { get; init; }
    [IpcOptional, IpcNumber] public long? FirstMac { get; init; }
    [IpcOptional, IpcNumber] public long? FirstLinux { get; init; }
    [IpcOptional, IpcNumber] public long? FirstDeck { get; init; }
    [IpcOptional, IpcNumber] public long? LastWindows { get; init; }
    [IpcOptional, IpcNumber] public long? LastMac { get; init; }
    [IpcOptional, IpcNumber] public long? LastLinux { get; init; }
    [IpcOptional, IpcNumber] public long? LastDeck { get; init; }
    [IpcNumber] public required long ObservedAt { get; init; }
}

public sealed record PlatformOverviewDto
{
    public required IReadOnlyList<PlatformPlaytimeDto> Games { get; init; }
    public required IReadOnlyDictionary<string, string> Names { get; init; }
}

public sealed record InstallEventDto
{
    [IpcStringValues("baseline", "installed", "uninstalled")] public required string Kind { get; init; }
    [IpcNumber] public required long ObservedAt { get; init; }
    [IpcOptional, IpcNumber] public long? SizeOnDisk { get; init; }
    [IpcOptional] public int? BuildId { get; init; }
}

public sealed record BuildEventDto
{
    public required int BuildId { get; init; }
    [IpcNumber] public required long ObservedAt { get; init; }
    public required bool IsBaseline { get; init; }
}

public sealed record ArtifactEventDto
{
    public required int Id { get; init; }
    public required int AppId { get; init; }
    public required string Name { get; init; }
    [IpcStringValues("delisted", "relisted", "renamed", "coverChanged", "publisherChanged", "serverShutdown")] public required string Kind { get; init; }
    [IpcNumber] public required long ObservedAt { get; init; }
    [IpcOptional] public string? Before { get; init; }
    [IpcOptional] public string? After { get; init; }
}

public sealed record StoreSnapshotDto
{
    public required int Id { get; init; }
    public required int AppId { get; init; }
    [IpcNumber] public required long CapturedAt { get; init; }
    public required string Name { get; init; }
    [IpcOptional] public string? Description { get; init; }
    public required IReadOnlyList<string> Developers { get; init; }
    public required IReadOnlyList<string> Publishers { get; init; }
    [IpcOptional] public string? Franchise { get; init; }
    [IpcOptional, IpcNumber] public long? ReleaseDate { get; init; }
    /// <summary>本地保存的封面路径，没有时用 HeaderUrl。</summary>
    [IpcOptional] public string? CoverPath { get; init; }
    [IpcOptional] public string? HeaderUrl { get; init; }
    public required IReadOnlyList<string> Screenshots { get; init; }
}

public sealed record GameArchiveDto
{
    public required IReadOnlyList<PlatformPlaytimeDto> Platforms { get; init; }
    public required IReadOnlyList<InstallEventDto> Installs { get; init; }
    public required IReadOnlyList<BuildEventDto> Builds { get; init; }
    public required IReadOnlyList<ArtifactEventDto> Artifacts { get; init; }
    public required IReadOnlyList<StoreSnapshotDto> Snapshots { get; init; }
}

public sealed record PlayerCurveRequest
{
    [IpcRange(1, int.MaxValue)] public required int AppId { get; init; }
    [IpcOptional, IpcRange(1, 240)] public int? Months { get; init; }
}

public sealed record PlayerPointDto
{
    /// <summary>UTC 当天 0 点。</summary>
    [IpcNumber] public required long Day { get; init; }
    public required int Players { get; init; }
    [IpcStringValues("public", "local")] public required string Source { get; init; }
}

public sealed record PlayerCurveDto
{
    public required IReadOnlyList<PlayerPointDto> Points { get; init; }
    /// <summary>公共数据层关闭时只有本机采样。</summary>
    public required bool PublicEnabled { get; init; }
}

public sealed record RichPresenceMiningRequest
{
    [IpcRange(1, int.MaxValue)] public required int AppId { get; init; }
    /// <summary>不传时看本机账号自己的记录。</summary>
    [IpcOptional, IpcMaxLength(32)] public string? SteamId { get; init; }
}

public sealed record RichPresenceValueDto
{
    public required string Value { get; init; }
    [IpcNumber] public required long Seconds { get; init; }
    public required int Times { get; init; }
    [IpcNumber] public required long FirstAt { get; init; }
    [IpcNumber] public required long LastAt { get; init; }
}

public sealed record RichPresenceDimensionDto
{
    [IpcStringValues("map", "mode", "rank", "character", "difficulty", "activity", "party")] public required string Kind { get; init; }
    public required string Key { get; init; }
    public required IReadOnlyList<RichPresenceValueDto> Values { get; init; }
}

public sealed record RichPresenceChangeDto
{
    [IpcNumber] public required long At { get; init; }
    public required string Value { get; init; }
}

public sealed record RichPresenceMiningDto
{
    [IpcNumber] public required long RecordedSeconds { get; init; }
    public required int Records { get; init; }
    [IpcOptional, IpcNumber] public long? FirstAt { get; init; }
    [IpcOptional, IpcNumber] public long? LastAt { get; init; }
    public required IReadOnlyList<RichPresenceDimensionDto> Dimensions { get; init; }
    public required IReadOnlyList<RichPresenceChangeDto> RankHistory { get; init; }
    public required IReadOnlyList<string> UnknownKeys { get; init; }
}

// ---------------------------------------------------------------------------
// 消费档案
// ---------------------------------------------------------------------------

public sealed record PurchasePageRequest
{
    [IpcOptional] public IReadOnlyList<string>? SteamIds { get; init; }
    [IpcRange(0, int.MaxValue)] public required int Offset { get; init; }
    [IpcRange(1, 2000)] public required int Limit { get; init; }
}

public sealed record PurchaseItemDto
{
    public required string Name { get; init; }
    [IpcOptional] public int? AppId { get; init; }
    [IpcStringValues("exact", "edition", "license", "none")] public required string Source { get; init; }
    [IpcNumber] public required long AllocatedCents { get; init; }
    public required bool Shared { get; init; }
}

public sealed record PurchaseDto
{
    public required int Id { get; init; }
    public required string SteamId { get; init; }
    [IpcNumber] public required long Date { get; init; }
    [IpcStringValues("purchase", "gift", "ingame", "market", "wallet", "refund", "other")] public required string Kind { get; init; }
    public required string TypeText { get; init; }
    [IpcNumber] public required long TotalCents { get; init; }
    [IpcOptional] public string? Currency { get; init; }
    [IpcOptional, IpcNumber] public long? WalletChangeCents { get; init; }
    [IpcOptional] public string? PaymentMethod { get; init; }
    public required bool Refunded { get; init; }
    public required IReadOnlyList<PurchaseItemDto> Items { get; init; }
}

public sealed record PurchasePageDto
{
    public required IReadOnlyList<PurchaseDto> Items { get; init; }
    public required int Total { get; init; }
    [IpcOptional] public int? NextOffset { get; init; }
}

// ---------------------------------------------------------------------------
// 愿望单
// ---------------------------------------------------------------------------

public sealed record WishlistItemDto
{
    public required string SteamId { get; init; }
    public required int AppId { get; init; }
    public required string Name { get; init; }
    [IpcNumber] public required long AddedAt { get; init; }
    [IpcOptional, IpcNumber] public long? RemovedAt { get; init; }
    [IpcOptional] public int? Priority { get; init; }
    [IpcStringValues("waiting", "acquired", "removed")] public required string Outcome { get; init; }
    [IpcOptional, IpcNumber] public long? AcquiredAt { get; init; }
    public required int WaitDays { get; init; }
    [IpcOptional] public int? PriceCents { get; init; }
    [IpcOptional] public int? ReviewPercent { get; init; }
    public required bool Delisted { get; init; }
}

public sealed record WishlistArchiveDto
{
    public required IReadOnlyList<WishlistItemDto> Items { get; init; }
}

// ---------------------------------------------------------------------------
// 数字文物
// ---------------------------------------------------------------------------

public sealed record DelistedGameDto
{
    public required int AppId { get; init; }
    public required string Name { get; init; }
    [IpcNumber] public required long DelistedAt { get; init; }
    public required bool HasSnapshot { get; init; }
}

public sealed record ArtifactOverviewDto
{
    public required IReadOnlyList<ArtifactEventDto> Events { get; init; }
    public required IReadOnlyList<DelistedGameDto> Delisted { get; init; }
    public required int Snapshots { get; init; }
    public required int Games { get; init; }
    public required int Covers { get; init; }
    [IpcNumber] public required long CoverBytes { get; init; }
}

// ---------------------------------------------------------------------------
// 社区足迹
// ---------------------------------------------------------------------------

public sealed record CommunityReviewDto
{
    public required string SteamId { get; init; }
    public required string ReviewId { get; init; }
    public required int AppId { get; init; }
    public required string Name { get; init; }
    public required bool Recommended { get; init; }
    [IpcOptional] public double? HoursTotal { get; init; }
    [IpcOptional] public double? HoursAtReview { get; init; }
    [IpcOptional, IpcNumber] public long? PostedAt { get; init; }
    [IpcOptional, IpcNumber] public long? EditedAt { get; init; }
    public required string Text { get; init; }
    public required int Helpful { get; init; }
    public required int Funny { get; init; }
    [IpcOptional, IpcNumber] public long? MissingSince { get; init; }
}

public sealed record CommunityFileDto
{
    public required string SteamId { get; init; }
    [IpcStringValues("guides", "favorites")] public required string Kind { get; init; }
    public required string PublishedFileId { get; init; }
    public required int AppId { get; init; }
    public required string Title { get; init; }
    [IpcOptional] public string? Description { get; init; }
    [IpcOptional] public string? Author { get; init; }
    [IpcOptional] public string? PreviewUrl { get; init; }
    [IpcOptional, IpcNumber] public long? MissingSince { get; init; }
}

public sealed record CommunityArchiveDto
{
    public required IReadOnlyList<CommunityReviewDto> Reviews { get; init; }
    public required IReadOnlyList<CommunityFileDto> Files { get; init; }
    public required int Comments { get; init; }
}

public sealed record CommunityCommentsRequest
{
    [IpcOptional] public IReadOnlyList<string>? SteamIds { get; init; }
    [IpcRange(0, int.MaxValue)] public required int Offset { get; init; }
    [IpcRange(1, 500)] public required int Limit { get; init; }
}

public sealed record CommunityCommentDto
{
    public required string SteamId { get; init; }
    public required string CommentId { get; init; }
    [IpcOptional] public string? AuthorSteamId { get; init; }
    public required string AuthorName { get; init; }
    [IpcNumber] public required long PostedAt { get; init; }
    public required string Text { get; init; }
    [IpcOptional, IpcNumber] public long? MissingSince { get; init; }
}

public sealed record CommunityCommentPageDto
{
    public required IReadOnlyList<CommunityCommentDto> Items { get; init; }
    public required int Total { get; init; }
    [IpcOptional] public int? NextOffset { get; init; }
}

// ---------------------------------------------------------------------------
// 家庭共享
// ---------------------------------------------------------------------------

public sealed record FamilyMemberDto
{
    public required string SteamId { get; init; }
    [IpcOptional] public string? Name { get; init; }
    public required int Role { get; init; }
    [IpcOptional, IpcNumber] public long? JoinedAt { get; init; }
    public required bool Former { get; init; }
}

public sealed record FamilyPlaytimeDto
{
    public required string SteamId { get; init; }
    public required int AppId { get; init; }
    public required string Name { get; init; }
    public required bool ByOwner { get; init; }
    [IpcOptional, IpcNumber] public long? FirstPlayed { get; init; }
    [IpcOptional, IpcNumber] public long? LatestPlayed { get; init; }
    [IpcNumber] public required long Seconds { get; init; }
}

public sealed record FamilyArchiveDto
{
    public required IReadOnlyList<FamilyMemberDto> Members { get; init; }
    public required IReadOnlyList<FamilyPlaytimeDto> Playtime { get; init; }
}

// ---------------------------------------------------------------------------
// 录像
// ---------------------------------------------------------------------------

public sealed record RecordingMarkerDto
{
    public required string Kind { get; init; }
    public required string Title { get; init; }
    [IpcOptional] public string? Group { get; init; }
    [IpcNumber] public required long OffsetMs { get; init; }
    [IpcOptional, IpcNumber] public long? DurationMs { get; init; }
}

public sealed record RecordingTimelineDto
{
    public required int Id { get; init; }
    public required string SteamId { get; init; }
    public required string TimelineId { get; init; }
    public required int AppId { get; init; }
    public required string Name { get; init; }
    [IpcNumber] public required long StartedAt { get; init; }
    [IpcNumber] public required long DurationMs { get; init; }
    public required IReadOnlyList<RecordingMarkerDto> Markers { get; init; }
}

public sealed record RecordingMediaDto
{
    public required int Id { get; init; }
    public required string SteamId { get; init; }
    public required string MediaId { get; init; }
    [IpcStringValues("background", "clip")] public required string Kind { get; init; }
    public required int AppId { get; init; }
    public required string Name { get; init; }
    [IpcOptional] public string? Title { get; init; }
    [IpcNumber] public required long StartedAt { get; init; }
    [IpcNumber] public required long DurationMs { get; init; }
    [IpcNumber] public required long SizeBytes { get; init; }
    [IpcOptional] public string? ThumbnailPath { get; init; }
    public required IReadOnlyList<string> TimelineIds { get; init; }
    public required IReadOnlyList<RecordingMarkerDto> Markers { get; init; }
    [IpcOptional, IpcNumber] public long? MissingSince { get; init; }
}

public sealed record RecordingArchiveDto
{
    public required IReadOnlyList<RecordingTimelineDto> Timelines { get; init; }
    public required IReadOnlyList<RecordingMediaDto> Media { get; init; }
}

public sealed record RecordingActionRequest
{
    [IpcRange(1, int.MaxValue)] public required int Id { get; init; }
}

// ---------------------------------------------------------------------------
// 截图色谱和海报
// ---------------------------------------------------------------------------

public sealed record ScreenshotPaletteImageRequest
{
    [IpcRange(1, int.MaxValue)] public required int Id { get; init; }
}

public sealed record ScreenshotPaletteRequest
{
    [IpcRange(1, 200)] public required int Limit { get; init; }
}

public sealed record ScreenshotPaletteItem
{
    [IpcRange(1, int.MaxValue)] public required int Id { get; init; }
    /// <summary>逗号分隔的十六进制颜色，空字符串表示这张读不出来，不再重试。</summary>
    [IpcMaxLength(64)] public required string Palette { get; init; }
}

public sealed record ScreenshotPaletteSaveRequest
{
    public required IReadOnlyList<ScreenshotPaletteItem> Items { get; init; }
}

/// <summary>并排的数组：一年几千张截图，每张一个对象太占消息体积。</summary>
public sealed record ScreenshotSpectrumDto
{
    public required IReadOnlyList<int> Times { get; init; }
    public required IReadOnlyList<int> AppIds { get; init; }
    public required IReadOnlyList<string> Colors { get; init; }
    public required int Pending { get; init; }
}

public sealed record PosterSaveRequest
{
    [IpcMaxLength(128)] public required string FileName { get; init; }
    [IpcMaxLength(33554432)] public required string Svg { get; init; }
}

public sealed record PosterSaveResultDto
{
    [IpcStringValues("saved", "cancelled", "failed")] public required string Status { get; init; }
    [IpcOptional] public string? ExportId { get; init; }
}

// ---------------------------------------------------------------------------
// 先行者指数、购前模拟、好友时长
// ---------------------------------------------------------------------------

public sealed record PioneerEntryDto
{
    public required int AppId { get; init; }
    public required string AppName { get; init; }
    public required string InternalName { get; init; }
    public required string Name { get; init; }
    public required string Icon { get; init; }
    [IpcNumber] public required long UnlockTime { get; init; }
    public required double FirstPercent { get; init; }
    public required double CurrentPercent { get; init; }
    public required double Lead { get; init; }
}

public sealed record PioneerReportDto
{
    [IpcOptional] public double? Score { get; init; }
    public required int Fresh { get; init; }
    public required int Matured { get; init; }
    public required int Late { get; init; }
    public required IReadOnlyList<PioneerEntryDto> Top { get; init; }
}

public sealed record StoreRegionDto
{
    /// <summary>现在生效的地区。</summary>
    public required string Region { get; init; }
    [IpcStringValues("manual", "account", "billing", "language")] public required string Source { get; init; }
    /// <summary>不手动设置时会用的地区，设置页的「自动」选项显示它。</summary>
    public required string Detected { get; init; }
}

public sealed record SimulatorSearchRequest
{
    [IpcMaxLength(128)] public required string Term { get; init; }
}

public sealed record SimulatorHitDto
{
    public required int AppId { get; init; }
    public required string Name { get; init; }
    [IpcOptional] public string? Image { get; init; }
    public required bool Owned { get; init; }
}

public sealed record SimulatorRunRequest
{
    [IpcRange(1, int.MaxValue)] public required int AppId { get; init; }
    /// <summary>用户自己填的价格，不填时用美区标价。</summary>
    [IpcOptional, IpcNumber, IpcRange(0, 100000000)] public long? PriceCents { get; init; }
    [IpcOptional] public IReadOnlyList<string>? SteamIds { get; init; }
}

public sealed record SimulatorNeighborDto
{
    public required int AppId { get; init; }
    public required string Name { get; init; }
    public required double Similarity { get; init; }
    public required double Hours { get; init; }
    public required bool Dropped { get; init; }
}

public sealed record SimulatorResultDto
{
    public required int AppId { get; init; }
    public required string Name { get; init; }
    [IpcStringValues("ok", "notFound", "noTags", "unavailable")] public required string Status { get; init; }
    public required bool Enough { get; init; }
    public required int Candidates { get; init; }
    /// <summary>库里有标签可比的游戏数。标签随每周的商店检查慢慢补齐。</summary>
    public required int TaggedGames { get; init; }
    [IpcOptional] public double? MedianHours { get; init; }
    [IpcOptional] public double? LowHours { get; init; }
    [IpcOptional] public double? HighHours { get; init; }
    [IpcOptional] public double? DropProbability { get; init; }
    [IpcOptional] public double? NeverPlayedProbability { get; init; }
    /// <summary>这一区的现价，打折时是折后价。用户手填时是手填的值。</summary>
    [IpcOptional, IpcNumber] public long? PriceCents { get; init; }
    public required bool PriceIsManual { get; init; }
    /// <summary>商店显示的价格文字，手填时为空。</summary>
    [IpcOptional] public string? PriceText { get; init; }
    [IpcOptional] public string? Currency { get; init; }
    public required string Region { get; init; }
    /// <summary>这款游戏在这个区看不到，内容是从美区查到的，没有这个区的价格。</summary>
    public required bool RegionUnavailable { get; init; }
    [IpcOptional] public double? CostPerHourCents { get; init; }
    public required IReadOnlyList<SimulatorNeighborDto> Neighbors { get; init; }
}

public sealed record FriendPlaytimeRequest
{
    [IpcMaxLength(32)] public required string SteamId { get; init; }
    [IpcOptional, IpcRange(1, 3650)] public int? Days { get; init; }
}

public sealed record FriendPlaytimeGameDto
{
    public required int AppId { get; init; }
    public required string Name { get; init; }
    public required int Minutes { get; init; }
    public required int DeltaMinutes { get; init; }
    [IpcNumber] public required long LastChangedAt { get; init; }
}

public sealed record FriendPlaytimeDto
{
    public required IReadOnlyList<FriendPlaytimeGameDto> Games { get; init; }
    [IpcOptional, IpcNumber] public long? BaselineAt { get; init; }
}
