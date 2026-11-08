namespace SteamStat.Contracts.Ipc;

// ---------------------------------------------------------------------------
// 回忆录：时光机、人生章节、史前考古、时光胶囊、回坑「上回说到」、自包含静态站
//
// 证据分级（Evidence）：
//   recorded  Steam Stat 自己记录的
//   official  Steam 官方给的：首玩时间、解锁时间、许可证时间、时长快照、年度回顾
//   inferred  推算的，界面带「推算」标记
//   unknown   无从知晓，界面灰色占位
//
// 渲染层传给 Host 的日期一律是「距 1970-01-01 的天数」或 Unix 秒：桥接层会把日期字符串转成 DateTime。
// 用户写的文字（标题、备注、胶囊正文、别名）同理编码成「s1:」+ Base64 再传，长度上限按编码后计。
// ---------------------------------------------------------------------------

/// <summary>一个带证据等级的数字。推算或未知时 Upper 是已知的上限（比如安装时的官方时长）。</summary>
public sealed record MemoirValueDto
{
    [IpcNumber] public required long Value { get; init; }
    [IpcStringValues("recorded", "official", "inferred", "unknown")] public required string Evidence { get; init; }
    [IpcOptional, IpcNumber] public long? Upper { get; init; }
}

public sealed record MemoirTraceDto
{
    public required int AppId { get; init; }
    public required string Name { get; init; }
    [IpcStringValues("achievement", "screenshot", "cloudSave", "firstPlay")] public required string Kind { get; init; }
    [IpcNumber] public required long Time { get; init; }
}

public sealed record MemoirUnlockDto
{
    public required int AppId { get; init; }
    public required string AppName { get; init; }
    public required string InternalName { get; init; }
    public required string Name { get; init; }
    [IpcOptional] public string? Icon { get; init; }
    [IpcNumber] public required long Time { get; init; }
    [IpcOptional] public double? GlobalPercent { get; init; }
}

/// <summary>章节的引用：时光机里显示「这一天属于哪一章」。</summary>
public sealed record MemoirChapterRefDto
{
    public required int Index { get; init; }
    [IpcStringValues("quiet", "era", "duo", "sampler", "variety", "mostly")] public required string Kind { get; init; }
    public required IReadOnlyList<int> AppIds { get; init; }
    public required string FromMonth { get; init; }
    public required string ToMonth { get; init; }
}

// ---------------------------------------------------------------------------
// 人生事件
// ---------------------------------------------------------------------------

public sealed record LifeEventDto
{
    public required int Id { get; init; }
    public required string Title { get; init; }
    [IpcOptional] public string? Note { get; init; }
    [IpcStringValues("study", "work", "move", "travel", "family", "relationship", "health", "milestone", "other")] public required string Kind { get; init; }
    /// <summary>距 1970-01-01 的天数。</summary>
    public required int StartDay { get; init; }
    [IpcOptional] public int? EndDay { get; init; }
    /// <summary>yyyy-MM-dd，只用于显示。</summary>
    public required string Start { get; init; }
    [IpcOptional] public string? End { get; init; }
    /// <summary>从这里开始新的一章。</summary>
    public required bool Boundary { get; init; }
    [IpcNumber] public required long UpdatedAt { get; init; }
}

public sealed record LifeEventSaveRequest
{
    [IpcOptional] public int? Id { get; init; }
    /// <summary>s1: 编码，解码后最多 80 字。</summary>
    [IpcMaxLength(440)] public required string Title { get; init; }
    /// <summary>s1: 编码，解码后最多 2000 字。</summary>
    [IpcOptional, IpcMaxLength(8100)] public string? Note { get; init; }
    [IpcStringValues("study", "work", "move", "travel", "family", "relationship", "health", "milestone", "other")] public required string Kind { get; init; }
    [IpcRange(0, 80000)] public required int StartDay { get; init; }
    [IpcOptional, IpcRange(0, 80000)] public int? EndDay { get; init; }
    public required bool Boundary { get; init; }
}

public sealed record LifeEventIdRequest
{
    [IpcRange(1, int.MaxValue)] public required int Id { get; init; }
}

// ---------------------------------------------------------------------------
// 时光机
// ---------------------------------------------------------------------------

public sealed record TimeMachineRequest
{
    /// <summary>逻辑日，距 1970-01-01 的天数。</summary>
    [IpcRange(0, 80000)] public required int Day { get; init; }
    [IpcOptional] public IReadOnlyList<string>? SteamIds { get; init; }
}

public sealed record TimeMachineSessionDto
{
    public required string SteamId { get; init; }
    public required int AppId { get; init; }
    public required string Name { get; init; }
    [IpcNumber] public required long Start { get; init; }
    [IpcNumber] public required long Seconds { get; init; }
}

public sealed record TimeMachineGameDto
{
    public required int AppId { get; init; }
    public required string Name { get; init; }
    [IpcNumber] public required long Seconds { get; init; }
    [IpcStringValues("recorded", "official", "inferred", "unknown")] public required string Evidence { get; init; }
}

public sealed record MemoirOfficialMonthDto
{
    public required string Month { get; init; }
    [IpcOptional, IpcNumber] public long? Seconds { get; init; }
    public required int Games { get; init; }
    public required IReadOnlyList<int> AppIds { get; init; }
}

public sealed record TimeMachineProfileDto
{
    [IpcOptional] public string? PersonaName { get; init; }
    [IpcOptional] public int? Level { get; init; }
    [IpcStringValues("recorded", "official", "inferred", "unknown")] public required string Evidence { get; init; }
    /// <summary>这条资料是什么时候观测到的。</summary>
    [IpcOptional, IpcNumber] public long? ObservedAt { get; init; }
}

public sealed record TimeMachineSummaryDto
{
    public required MemoirValueDto DaySeconds { get; init; }
    public required MemoirValueDto WeekSeconds { get; init; }
    public required MemoirValueDto MonthSeconds { get; init; }
    public required MemoirValueDto TotalSeconds { get; init; }
    /// <summary>到这一天为止玩过多少款游戏。</summary>
    public required MemoirValueDto GamesPlayed { get; init; }
    public required IReadOnlyList<TimeMachineSessionDto> Sessions { get; init; }
    public required IReadOnlyList<MemoirTraceDto> Traces { get; init; }
    public required IReadOnlyList<TimeMachineGameDto> RecentGames { get; init; }
    [IpcOptional] public MemoirOfficialMonthDto? OfficialMonth { get; init; }
    [IpcOptional] public TimeMachineProfileDto? Profile { get; init; }
    [IpcOptional] public MemoirChapterRefDto? Chapter { get; init; }
    public required IReadOnlyList<LifeEventDto> LifeEvents { get; init; }
}

public sealed record TimeMachineLibraryGameDto
{
    public required int AppId { get; init; }
    public required string Name { get; init; }
    [IpcOptional, IpcNumber] public long? AcquiredAt { get; init; }
    [IpcStringValues("recorded", "official", "inferred", "unknown")] public required string AcquiredEvidence { get; init; }
    public required MemoirValueDto Playtime { get; init; }
    [IpcOptional, IpcNumber] public long? FirstPlayed { get; init; }
    /// <summary>这一天之前最后一次游玩（记录或官方首玩），没有时为空。</summary>
    [IpcOptional, IpcNumber] public long? LastPlayed { get; init; }
}

public sealed record TimeMachineLibraryDto
{
    /// <summary>这一天已入库的游戏数。Upper 把入库时间未知的也算上。</summary>
    public required MemoirValueDto Owned { get; init; }
    /// <summary>现在拥有、但不知道什么时候入库的。</summary>
    public required int Unknown { get; init; }
    /// <summary>这一天之后才入库的。</summary>
    public required int Later { get; init; }
    public required IReadOnlyList<TimeMachineLibraryGameDto> Games { get; init; }
}

public sealed record TimeMachineProgressDto
{
    public required int AppId { get; init; }
    public required string Name { get; init; }
    public required int Unlocked { get; init; }
    public required int Total { get; init; }
    /// <summary>这一天之后 schema 改过时分母是推算的。</summary>
    [IpcStringValues("recorded", "official", "inferred", "unknown")] public required string Evidence { get; init; }
}

public sealed record TimeMachineAchievementsDto
{
    public required MemoirValueDto Unlocked { get; init; }
    public required MemoirValueDto Completed { get; init; }
    public required int Games { get; init; }
    public required IReadOnlyList<MemoirUnlockDto> DayUnlocks { get; init; }
    public required IReadOnlyList<MemoirUnlockDto> Recent { get; init; }
    public required IReadOnlyList<MemoirUnlockDto> Rarest { get; init; }
    public required IReadOnlyList<TimeMachineProgressDto> Progress { get; init; }
}

public sealed record TimeMachineFriendDto
{
    public required string SteamId { get; init; }
    /// <summary>那时候的名字。</summary>
    public required string Name { get; init; }
    [IpcStringValues("recorded", "official", "inferred", "unknown")] public required string Evidence { get; init; }
    [IpcOptional, IpcNumber] public long? AddedAt { get; init; }
    [IpcOptional, IpcNumber] public long? RemovedAt { get; init; }
}

public sealed record TimeMachineFriendPlayDto
{
    public required string SteamId { get; init; }
    public required string Name { get; init; }
    public required int AppId { get; init; }
    public required string GameName { get; init; }
    [IpcNumber] public required long Seconds { get; init; }
}

public sealed record TimeMachineFriendsDto
{
    public required MemoirValueDto Count { get; init; }
    /// <summary>从哪天开始知道好友列表的变化，之前加的好友不知道是什么时候加的。</summary>
    [IpcOptional] public string? KnownSince { get; init; }
    public required IReadOnlyList<TimeMachineFriendDto> Friends { get; init; }
    public required IReadOnlyList<TimeMachineFriendPlayDto> Activity { get; init; }
    public required IReadOnlyList<string> Added { get; init; }
    public required IReadOnlyList<string> Removed { get; init; }
}

public sealed record TimeMachineDto
{
    public required string Day { get; init; }
    public required int DayNumber { get; init; }
    public required string Today { get; init; }
    public required int TodayNumber { get; init; }
    /// <summary>最早的证据（首玩、痕迹、记录）所在的逻辑日。</summary>
    [IpcOptional] public string? FirstDay { get; init; }
    [IpcOptional] public int? FirstDayNumber { get; init; }
    /// <summary>Steam Stat 开始记录的那一天，之前都是史前。</summary>
    [IpcOptional] public string? TrackingStartDay { get; init; }
    public required TimeMachineSummaryDto Summary { get; init; }
    public required TimeMachineLibraryDto Library { get; init; }
    public required TimeMachineAchievementsDto Achievements { get; init; }
    public required TimeMachineFriendsDto Friends { get; init; }
}

// ---------------------------------------------------------------------------
// 人生章节
// ---------------------------------------------------------------------------

public sealed record MemoirMonthGameDto
{
    public required int AppId { get; init; }
    [IpcNumber] public required long Seconds { get; init; }
}

public sealed record MemoirMonthDto
{
    /// <summary>yyyy-MM</summary>
    public required string Month { get; init; }
    [IpcNumber] public required long Seconds { get; init; }
    [IpcStringValues("recorded", "official", "inferred", "unknown")] public required string Evidence { get; init; }
    public required IReadOnlyList<MemoirMonthGameDto> Games { get; init; }
}

public sealed record MemoirChapterGameDto
{
    public required int AppId { get; init; }
    public required string Name { get; init; }
    [IpcNumber] public required long Seconds { get; init; }
    public required double Share { get; init; }
}

public sealed record MemoirChapterDto
{
    public required int Index { get; init; }
    public required string FromMonth { get; init; }
    public required string ToMonth { get; init; }
    public required int Months { get; init; }
    [IpcStringValues("quiet", "era", "duo", "sampler", "variety", "mostly")] public required string Kind { get; init; }
    public required IReadOnlyList<int> AppIds { get; init; }
    [IpcNumber] public required long Seconds { get; init; }
    [IpcNumber] public required long MonthlySeconds { get; init; }
    public required string PeakMonth { get; init; }
    public required int NewGames { get; init; }
    public required int Unlocks { get; init; }
    /// <summary>0~1，记录的会话里深夜（0~5 点）开始的比例；没有记录时为空。</summary>
    [IpcOptional] public double? NightShare { get; init; }
    public required IReadOnlyList<MemoirChapterGameDto> TopGames { get; init; }
    [IpcStringValues("recorded", "official", "inferred", "unknown")] public required string Evidence { get; init; }
    /// <summary>章节从这个人生事件开始（事件设为分界点时）。</summary>
    [IpcOptional] public int? StartEventId { get; init; }
}

public sealed record MemoirChaptersDto
{
    public required IReadOnlyList<MemoirMonthDto> Months { get; init; }
    public required IReadOnlyList<MemoirChapterDto> Chapters { get; init; }
    public required IReadOnlyList<LifeEventDto> Events { get; init; }
    public required IReadOnlyDictionary<string, string> Names { get; init; }
    [IpcOptional] public string? TrackingStartMonth { get; init; }
}

// ---------------------------------------------------------------------------
// 史前考古
// ---------------------------------------------------------------------------

public sealed record MemoirFirstPlayDto
{
    public required int AppId { get; init; }
    public required string Name { get; init; }
    [IpcNumber] public required long FirstPlayed { get; init; }
    public required bool BeforeTracking { get; init; }
}

public sealed record MemoirUntrackedDto
{
    public required int AppId { get; init; }
    public required string Name { get; init; }
    [IpcNumber] public required long OfficialSeconds { get; init; }
    [IpcNumber] public required long RecordedSeconds { get; init; }
    [IpcNumber] public required long UntrackedSeconds { get; init; }
    /// <summary>官方时长第一次被观测到的时间（安装时的基线）。没有时差额是推算的。</summary>
    [IpcOptional, IpcNumber] public long? BaselineAt { get; init; }
    [IpcStringValues("recorded", "official", "inferred", "unknown")] public required string Evidence { get; init; }
}

public sealed record MemoirTraceDayDto
{
    public required string Day { get; init; }
    public required IReadOnlyList<int> AppIds { get; init; }
    public required IReadOnlyList<string> Kinds { get; init; }
    public required int Count { get; init; }
}

public sealed record MemoirTraceCountDto
{
    [IpcStringValues("achievement", "screenshot", "cloudSave", "firstPlay")] public required string Kind { get; init; }
    public required int Total { get; init; }
    public required int BeforeTracking { get; init; }
}

public sealed record YearInReviewStreakDto
{
    public required int Days { get; init; }
    [IpcOptional, IpcNumber] public long? Start { get; init; }
    public required IReadOnlyList<int> AppIds { get; init; }
}

public sealed record YearInReviewMonthDto
{
    public required int Month { get; init; }
    [IpcOptional, IpcNumber] public long? Seconds { get; init; }
    /// <summary>占全年的比例 0~1。</summary>
    [IpcOptional] public double? Share { get; init; }
    [IpcOptional] public int? Sessions { get; init; }
    public required IReadOnlyList<int> AppIds { get; init; }
}

public sealed record YearInReviewGameDto
{
    public required int AppId { get; init; }
    public required string Name { get; init; }
    [IpcOptional, IpcNumber] public long? Seconds { get; init; }
    [IpcOptional] public double? Share { get; init; }
    [IpcOptional] public int? Sessions { get; init; }
    public required bool NewThisYear { get; init; }
    [IpcOptional, IpcNumber] public long? FirstPlayed { get; init; }
}

/// <summary>官方全球百分位与时长分布。字段含义待真实账号验证。</summary>
public sealed record YearInReviewPercentilesDto
{
    [IpcOptional] public int? MedianAchievements { get; init; }
    [IpcOptional] public int? MedianGames { get; init; }
    [IpcOptional] public int? MedianStreak { get; init; }
    [IpcOptional] public int? GamesPlayedPercentile { get; init; }
    [IpcOptional] public int? AchievementsPercentile { get; init; }
    [IpcOptional] public int? StreakPercentile { get; init; }
    [IpcOptional] public int? NewReleasesShare { get; init; }
    [IpcOptional] public int? RecentReleasesShare { get; init; }
    [IpcOptional] public int? ClassicReleasesShare { get; init; }
    [IpcOptional] public int? RecentCutoffYear { get; init; }
}

public sealed record YearInReviewDto
{
    public required string SteamId { get; init; }
    public required int Year { get; init; }
    [IpcNumber] public required long FetchedAt { get; init; }
    [IpcOptional, IpcNumber] public long? Seconds { get; init; }
    [IpcOptional] public int? Sessions { get; init; }
    public required int GamesPlayed { get; init; }
    public required int NewGames { get; init; }
    [IpcOptional] public int? Achievements { get; init; }
    [IpcOptional] public YearInReviewStreakDto? Streak { get; init; }
    public required IReadOnlyList<YearInReviewMonthDto> Months { get; init; }
    public required IReadOnlyList<YearInReviewGameDto> Games { get; init; }
    [IpcOptional] public YearInReviewPercentilesDto? Percentiles { get; init; }
    /// <summary>平台占比（windows / linux / mac / deck / vr / controller），0~1。</summary>
    public required IReadOnlyDictionary<string, double> Platforms { get; init; }
}

public sealed record MemoirPrehistoryDto
{
    public required string Today { get; init; }
    [IpcOptional] public string? TrackingStartDay { get; init; }
    [IpcOptional] public string? FirstEvidenceDay { get; init; }
    public required IReadOnlyList<MemoirFirstPlayDto> FirstPlays { get; init; }
    public required IReadOnlyList<MemoirUntrackedDto> Untracked { get; init; }
    public required IReadOnlyList<MemoirTraceDayDto> Calendar { get; init; }
    public required IReadOnlyList<MemoirTraceCountDto> TraceCounts { get; init; }
    public required IReadOnlyList<YearInReviewDto> YearReviews { get; init; }
    public required IReadOnlyDictionary<string, string> Names { get; init; }
}

// ---------------------------------------------------------------------------
// 时光胶囊
// ---------------------------------------------------------------------------

/// <summary>封存时的档案摘要，开启时和现在对比。</summary>
public sealed record TimeCapsuleContextDto
{
    [IpcNumber] public required long TotalSeconds { get; init; }
    public required int Games { get; init; }
    public required int Achievements { get; init; }
    [IpcOptional] public int? TopAppId { get; init; }
    [IpcOptional] public string? TopAppName { get; init; }
    [IpcOptional, IpcNumber] public long? AppSeconds { get; init; }
}

public sealed record TimeCapsuleDto
{
    public required int Id { get; init; }
    /// <summary>封存时可见的标签。正文到期前不发给渲染层。</summary>
    [IpcOptional] public string? Label { get; init; }
    [IpcNumber] public required long CreatedAt { get; init; }
    [IpcNumber] public required long OpenAt { get; init; }
    [IpcOptional] public int? AppId { get; init; }
    [IpcOptional] public string? AppName { get; init; }
    [IpcStringValues("sealed", "ready", "opened")] public required string State { get; init; }
    [IpcOptional, IpcNumber] public long? ReadyAt { get; init; }
    [IpcOptional, IpcNumber] public long? OpenedAt { get; init; }
    [IpcOptional] public string? Message { get; init; }
    [IpcOptional] public TimeCapsuleContextDto? Context { get; init; }
}

public sealed record TimeCapsuleCreateRequest
{
    /// <summary>s1: 编码，解码后最多 80 字。</summary>
    [IpcOptional, IpcMaxLength(440)] public string? Label { get; init; }
    /// <summary>s1: 编码，解码后最多 4000 字。</summary>
    [IpcMaxLength(16100)] public required string Message { get; init; }
    [IpcNumber] public required long OpenAt { get; init; }
    [IpcOptional, IpcRange(1, int.MaxValue)] public int? AppId { get; init; }
}

public sealed record TimeCapsuleIdRequest
{
    [IpcRange(1, int.MaxValue)] public required int Id { get; init; }
}

// ---------------------------------------------------------------------------
// 回坑「上回说到」
// ---------------------------------------------------------------------------

public sealed record PreviouslyOnRequest
{
    [IpcRange(1, int.MaxValue)] public required int AppId { get; init; }
    [IpcOptional] public IReadOnlyList<string>? SteamIds { get; init; }
}

public sealed record PreviouslyOnNoteDto
{
    public required string Text { get; init; }
    [IpcOptional, IpcNumber] public long? Time { get; init; }
    [IpcStringValues("session", "library")] public required string Source { get; init; }
}

public sealed record PreviouslyOnChangeDto
{
    [IpcStringValues("update", "achievements", "dlc")] public required string Kind { get; init; }
    public required int Count { get; init; }
    [IpcNumber] public required long Time { get; init; }
}

public sealed record PreviouslyOnPresenceDto
{
    public required string Text { get; init; }
    [IpcNumber] public required long Time { get; init; }
}

public sealed record PreviouslyOnProgressDto
{
    public required int Unlocked { get; init; }
    public required int Total { get; init; }
}

public sealed record PreviouslyOnDto
{
    public required int AppId { get; init; }
    public required string Name { get; init; }
    /// <summary>距上次游玩的天数。</summary>
    public required int GapDays { get; init; }
    [IpcNumber] public required long LastPlayedAt { get; init; }
    [IpcNumber] public required long TotalSeconds { get; init; }
    [IpcNumber] public required long LastSessionSeconds { get; init; }
    /// <summary>上次那段时间（最后一次游玩前 30 天）玩了多久、几局。</summary>
    [IpcNumber] public required long PeriodSeconds { get; init; }
    public required int PeriodSessions { get; init; }
    public required IReadOnlyList<PreviouslyOnNoteDto> Notes { get; init; }
    [IpcOptional] public string? Status { get; init; }
    public required IReadOnlyList<ScreenshotDto> Screenshots { get; init; }
    public required IReadOnlyList<MemoirUnlockDto> Unlocks { get; init; }
    [IpcOptional] public PreviouslyOnProgressDto? Progress { get; init; }
    [IpcOptional] public PreviouslyOnPresenceDto? Presence { get; init; }
    /// <summary>离开期间发生的事：版本更新、新成就、新 DLC。</summary>
    public required IReadOnlyList<PreviouslyOnChangeDto> Since { get; init; }
    /// <summary>这次启动开启的时光胶囊。</summary>
    public required IReadOnlyList<int> Capsules { get; init; }
    /// <summary>由这次启动触发（而不是在详情页手动查看）。</summary>
    public required bool Live { get; init; }
    [IpcNumber] public required long GeneratedAt { get; init; }
}

// ---------------------------------------------------------------------------
// 自包含静态站
// ---------------------------------------------------------------------------

public sealed record StaticSiteOptionsDto
{
    [IpcOptional] public IReadOnlyList<string>? SteamIds { get; init; }
    /// <summary>只导出这一年，空表示全部。</summary>
    [IpcOptional, IpcRange(2000, 2200)] public int? Year { get; init; }
    /// <summary>s1: 编码，解码后最多 80 字。</summary>
    [IpcOptional, IpcMaxLength(440)] public string? Title { get; init; }
    [IpcStringValues("real", "alias", "none")] public required string Identity { get; init; }
    /// <summary>s1: 编码，解码后最多 40 字。</summary>
    [IpcOptional, IpcMaxLength(220)] public string? Alias { get; init; }
    [IpcMaxLength(12)] public required IReadOnlyList<string> Sections { get; init; }
    [IpcStringValues("show", "mask")] public required string GameNames { get; init; }
    [IpcMaxLength(5000)] public required IReadOnlyList<int> ExcludedAppIds { get; init; }
    [IpcStringValues("exact", "rounded")] public required string Hours { get; init; }
    [IpcStringValues("day", "month")] public required string DatePrecision { get; init; }
    /// <summary>引用 Steam CDN 的封面。关闭时页面不加载任何外部资源。</summary>
    public required bool Covers { get; init; }
    /// <summary>人生章节里显示人生事件的标题。</summary>
    public required bool LifeEvents { get; init; }
    public required bool Credit { get; init; }
    [IpcStringValues("zh", "en")] public required string Language { get; init; }
    [IpcStringValues("light", "dark", "auto")] public required string Theme { get; init; }
    [IpcOptional, IpcMaxLength(9)] public string? Accent { get; init; }
}

public sealed record StaticSitePreviewDto
{
    public required string Html { get; init; }
    public required int Bytes { get; init; }
    public required int Games { get; init; }
    /// <summary>打码规则检查出的问题：noData / realName / exactDates / exactHours / covers。</summary>
    public required IReadOnlyList<string> Warnings { get; init; }
}

public sealed record StaticSiteExportRequest
{
    public required StaticSiteOptionsDto Options { get; init; }
    /// <summary>file：单个 HTML 文件。folder：可以直接放到 GitHub Pages 的文件夹。</summary>
    [IpcStringValues("file", "folder")] public required string Format { get; init; }
}

public sealed record StaticSiteExportResultDto
{
    [IpcStringValues("saved", "cancelled", "failed")] public required string Status { get; init; }
    [IpcOptional] public string? ExportId { get; init; }
}

public sealed record StaticSiteRevealRequest
{
    [IpcMaxLength(64)] public required string ExportId { get; init; }
}
