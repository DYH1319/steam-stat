namespace SteamStat.Contracts.Ipc;

// ---------------------------------------------------------------------------
// 游戏规划：坑（开坑、完成预期、搁置、队列、完成评价）、心得、周目、自定义标签、游玩计划、年度挑战
//
// 这些数据属于「我」，按应用存，不分账号；状态、总评分、通关日期仍在按账号存的 LibraryGameMeta。
// 记录的 Id 是多设备同步 ID（32 位十六进制），跨设备不变，标签关联也引用它。
// 日期一律是 Unix 秒或「距 1970-01-01 的天数」；用户写的文字编码成「s1:」+ Base64 再传，长度上限按编码后计。
// ---------------------------------------------------------------------------

public sealed record GameMilestoneDto
{
    [IpcMaxLength(32)] public required string Id { get; init; }
    /// <summary>s1: 编码，解码后最多 120 字（返回时是明文）。</summary>
    [IpcMaxLength(600)] public required string Title { get; init; }
    [IpcOptional, IpcNumber] public long? DoneAt { get; init; }
}

public sealed record GameReviewScoresDto
{
    [IpcOptional, IpcRange(1, 10)] public int? Story { get; init; }
    [IpcOptional, IpcRange(1, 10)] public int? Gameplay { get; init; }
    [IpcOptional, IpcRange(1, 10)] public int? Visuals { get; init; }
    [IpcOptional, IpcRange(1, 10)] public int? Audio { get; init; }
    [IpcOptional, IpcRange(1, 10)] public int? Replay { get; init; }
}

public sealed record GameJourneyDto
{
    public required int AppId { get; init; }
    [IpcOptional, IpcNumber] public long? StartedAt { get; init; }
    [IpcOptional, IpcStringValues("story", "storyPlus", "achievements", "playtime", "custom", "none")] public string? GoalKind { get; init; }
    [IpcOptional] public int? GoalHours { get; init; }
    [IpcOptional] public int? GoalAchievementPercent { get; init; }
    [IpcOptional] public string? GoalNote { get; init; }
    public required IReadOnlyList<GameMilestoneDto> Milestones { get; init; }
    [IpcOptional, IpcStringValues("beaten", "goalReached", "perfect")] public string? CompletionLevel { get; init; }
    [IpcOptional, IpcStringValues("noTime", "tooHard", "waitingUpdate", "waitingFriends", "burnedOut", "other")] public string? ShelvedReason { get; init; }
    [IpcOptional, IpcNumber] public long? ResumeAt { get; init; }
    [IpcOptional] public string? ResumeNote { get; init; }
    [IpcOptional] public double? QueueOrder { get; init; }
    [IpcOptional] public GameReviewScoresDto? ReviewScores { get; init; }
    [IpcOptional] public string? ReviewSummary { get; init; }
    [IpcOptional] public bool? Recommend { get; init; }
    [IpcOptional] public bool? WouldReplay { get; init; }
    [IpcNumber] public required long UpdatedAt { get; init; }
}

/// <summary>整行覆盖写入（队列顺序除外，它由 gameQueueOrderSet 管）；全部为空时删除这一行。</summary>
public sealed record GameJourneyInput
{
    [IpcRange(1, int.MaxValue)] public required int AppId { get; init; }
    [IpcOptional, IpcNumber] public long? StartedAt { get; init; }
    [IpcOptional, IpcStringValues("story", "storyPlus", "achievements", "playtime", "custom", "none")] public string? GoalKind { get; init; }
    [IpcOptional, IpcRange(1, 100000)] public int? GoalHours { get; init; }
    [IpcOptional, IpcRange(1, 100)] public int? GoalAchievementPercent { get; init; }
    /// <summary>s1: 编码，解码后最多 500 字。</summary>
    [IpcOptional, IpcMaxLength(2100)] public string? GoalNote { get; init; }
    [IpcOptional] public IReadOnlyList<GameMilestoneDto>? Milestones { get; init; }
    [IpcOptional, IpcStringValues("beaten", "goalReached", "perfect")] public string? CompletionLevel { get; init; }
    [IpcOptional, IpcStringValues("noTime", "tooHard", "waitingUpdate", "waitingFriends", "burnedOut", "other")] public string? ShelvedReason { get; init; }
    [IpcOptional, IpcNumber] public long? ResumeAt { get; init; }
    /// <summary>s1: 编码，解码后最多 500 字。</summary>
    [IpcOptional, IpcMaxLength(2100)] public string? ResumeNote { get; init; }
    [IpcOptional] public GameReviewScoresDto? ReviewScores { get; init; }
    /// <summary>s1: 编码，解码后最多 280 字。</summary>
    [IpcOptional, IpcMaxLength(1300)] public string? ReviewSummary { get; init; }
    [IpcOptional] public bool? Recommend { get; init; }
    [IpcOptional] public bool? WouldReplay { get; init; }
}

/// <summary>按顺序给出「下一个」队列里的游戏；不在列表里的游戏退出队列。</summary>
public sealed record GameQueueOrderSetRequest
{
    public required IReadOnlyList<int> AppIds { get; init; }
}

public sealed record GameJournalEntryDto
{
    public required string Id { get; init; }
    public required int AppId { get; init; }
    [IpcStringValues("note", "shelve", "milestone", "review", "resume")] public required string Kind { get; init; }
    public required string Body { get; init; }
    public required bool Spoiler { get; init; }
    [IpcOptional, IpcStringValues("great", "good", "meh", "tired", "frustrated", "moved")] public string? Mood { get; init; }
    [IpcNumber] public required long CreatedAt { get; init; }
    [IpcNumber] public required long UpdatedAt { get; init; }
}

public sealed record GameJournalSaveRequest
{
    [IpcOptional, IpcMaxLength(32)] public string? Id { get; init; }
    [IpcRange(1, int.MaxValue)] public required int AppId { get; init; }
    [IpcStringValues("note", "shelve", "milestone", "review", "resume")] public required string Kind { get; init; }
    /// <summary>s1: 编码，解码后最多 20000 字。</summary>
    [IpcMaxLength(80100)] public required string Body { get; init; }
    public required bool Spoiler { get; init; }
    [IpcOptional, IpcStringValues("great", "good", "meh", "tired", "frustrated", "moved")] public string? Mood { get; init; }
    /// <summary>可以补记以前的心得；为空时取现在。</summary>
    [IpcOptional, IpcNumber] public long? CreatedAt { get; init; }
}

/// <summary>AppId 为空时返回所有游戏里最近写的。</summary>
public sealed record GameJournalQueryRequest
{
    [IpcOptional, IpcRange(1, int.MaxValue)] public int? AppId { get; init; }
    [IpcOptional, IpcRange(1, 500)] public int? Limit { get; init; }
}

public sealed record GameJournalStatDto
{
    public required int AppId { get; init; }
    public required int Count { get; init; }
    [IpcNumber] public required long LastAt { get; init; }
}

public sealed record GamePlaythroughDto
{
    public required string Id { get; init; }
    public required int AppId { get; init; }
    [IpcOptional] public string? Label { get; init; }
    [IpcOptional, IpcNumber] public long? StartedAt { get; init; }
    [IpcOptional, IpcNumber] public long? EndedAt { get; init; }
    [IpcStringValues("ongoing", "completed", "abandoned")] public required string Outcome { get; init; }
    [IpcOptional, IpcStringValues("beaten", "goalReached", "perfect")] public string? CompletionLevel { get; init; }
    [IpcOptional] public int? PlaytimeMinutes { get; init; }
    [IpcOptional] public int? Rating { get; init; }
    [IpcOptional] public string? Note { get; init; }
    [IpcNumber] public required long UpdatedAt { get; init; }
}

public sealed record GamePlaythroughSaveRequest
{
    [IpcOptional, IpcMaxLength(32)] public string? Id { get; init; }
    [IpcRange(1, int.MaxValue)] public required int AppId { get; init; }
    /// <summary>s1: 编码，解码后最多 80 字。</summary>
    [IpcOptional, IpcMaxLength(440)] public string? Label { get; init; }
    [IpcOptional, IpcNumber] public long? StartedAt { get; init; }
    [IpcOptional, IpcNumber] public long? EndedAt { get; init; }
    [IpcStringValues("ongoing", "completed", "abandoned")] public required string Outcome { get; init; }
    [IpcOptional, IpcStringValues("beaten", "goalReached", "perfect")] public string? CompletionLevel { get; init; }
    [IpcOptional, IpcRange(0, 10_000_000)] public int? PlaytimeMinutes { get; init; }
    [IpcOptional, IpcRange(1, 10)] public int? Rating { get; init; }
    /// <summary>s1: 编码，解码后最多 2000 字。</summary>
    [IpcOptional, IpcMaxLength(8100)] public string? Note { get; init; }
}

public sealed record GameTagDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    [IpcStringValues("slate", "red", "orange", "amber", "lime", "green", "teal", "sky", "blue", "violet", "pink")] public required string Color { get; init; }
    [IpcOptional] public string? Icon { get; init; }
    public required int SortOrder { get; init; }
    [IpcNumber] public required long UpdatedAt { get; init; }
}

public sealed record GameTagSaveRequest
{
    [IpcOptional, IpcMaxLength(32)] public string? Id { get; init; }
    /// <summary>s1: 编码，解码后最多 24 字。</summary>
    [IpcMaxLength(140)] public required string Name { get; init; }
    [IpcStringValues("slate", "red", "orange", "amber", "lime", "green", "teal", "sky", "blue", "violet", "pink")] public required string Color { get; init; }
    [IpcOptional, IpcMaxLength(64)] public string? Icon { get; init; }
    [IpcOptional, IpcRange(0, 100000)] public int? SortOrder { get; init; }
}

public sealed record GameTagLinkDto
{
    public required string TagId { get; init; }
    public required int AppId { get; init; }
}

/// <summary>整体替换一款游戏的标签。</summary>
public sealed record GameTagLinksSetRequest
{
    [IpcRange(1, int.MaxValue)] public required int AppId { get; init; }
    public required IReadOnlyList<string> TagIds { get; init; }
}

/// <summary>给一批游戏加上或去掉同一个标签。</summary>
public sealed record GameTagAssignRequest
{
    [IpcMaxLength(32)] public required string TagId { get; init; }
    public required IReadOnlyList<int> AppIds { get; init; }
    public required bool Assigned { get; init; }
}

public sealed record PlayPlanDto
{
    public required string Id { get; init; }
    [IpcOptional] public int? AppId { get; init; }
    [IpcOptional] public string? Title { get; init; }
    [IpcStringValues("day", "range", "bucket")] public required string Kind { get; init; }
    public required int StartDay { get; init; }
    [IpcOptional] public int? EndDay { get; init; }
    [IpcOptional, IpcStringValues("week", "month", "quarter", "year")] public string? Bucket { get; init; }
    [IpcOptional] public int? EstimatedMinutes { get; init; }
    [IpcStringValues("pending", "done", "skipped")] public required string Status { get; init; }
    [IpcOptional] public string? Note { get; init; }
    [IpcNumber] public required long UpdatedAt { get; init; }
}

/// <summary>游戏和标题至少有一个。</summary>
public sealed record PlayPlanSaveRequest
{
    [IpcOptional, IpcMaxLength(32)] public string? Id { get; init; }
    [IpcOptional, IpcRange(1, int.MaxValue)] public int? AppId { get; init; }
    /// <summary>s1: 编码，解码后最多 80 字。</summary>
    [IpcOptional, IpcMaxLength(440)] public string? Title { get; init; }
    [IpcStringValues("day", "range", "bucket")] public required string Kind { get; init; }
    [IpcRange(0, 80000)] public required int StartDay { get; init; }
    [IpcOptional, IpcRange(0, 80000)] public int? EndDay { get; init; }
    [IpcOptional, IpcStringValues("week", "month", "quarter", "year")] public string? Bucket { get; init; }
    [IpcOptional, IpcRange(1, 100000)] public int? EstimatedMinutes { get; init; }
    [IpcStringValues("pending", "done", "skipped")] public required string Status { get; init; }
    /// <summary>s1: 编码，解码后最多 2000 字。</summary>
    [IpcOptional, IpcMaxLength(8100)] public string? Note { get; init; }
}

public sealed record PlayChallengeDto
{
    public required string Id { get; init; }
    public required int Year { get; init; }
    [IpcStringValues("completeGames", "clearBacklog", "startNew", "perfectGames", "playtimeHours")] public required string Kind { get; init; }
    public required int Target { get; init; }
    [IpcOptional] public string? Title { get; init; }
    [IpcNumber] public required long UpdatedAt { get; init; }
}

public sealed record PlayChallengeSaveRequest
{
    [IpcOptional, IpcMaxLength(32)] public string? Id { get; init; }
    [IpcRange(2000, 2200)] public required int Year { get; init; }
    [IpcStringValues("completeGames", "clearBacklog", "startNew", "perfectGames", "playtimeHours")] public required string Kind { get; init; }
    [IpcRange(1, 100000)] public required int Target { get; init; }
    /// <summary>s1: 编码，解码后最多 80 字。</summary>
    [IpcOptional, IpcMaxLength(440)] public string? Title { get; init; }
}

public sealed record PlannerItemDeleteRequest
{
    [IpcStringValues("journal", "playthrough", "tag", "plan", "challenge")] public required string Kind { get; init; }
    [IpcMaxLength(32)] public required string Id { get; init; }
}

/// <summary>规划页一次读完的全部数据；心得正文按游戏另取，这里只有每款游戏的条数。</summary>
public sealed record PlannerSnapshotDto
{
    public required IReadOnlyList<GameJourneyDto> Journeys { get; init; }
    public required IReadOnlyList<GamePlaythroughDto> Playthroughs { get; init; }
    public required IReadOnlyList<GameTagDto> Tags { get; init; }
    public required IReadOnlyList<GameTagLinkDto> TagLinks { get; init; }
    public required IReadOnlyList<PlayPlanDto> Plans { get; init; }
    public required IReadOnlyList<PlayChallengeDto> Challenges { get; init; }
    public required IReadOnlyList<GameJournalStatDto> JournalStats { get; init; }
}
