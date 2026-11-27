namespace ElectronNet.Models;

/// <summary>
/// 一款游戏的「坑」：开坑时间、完成预期、搁置信息、队列顺序和完成评价。属于「我」，不分账号，每个应用一行。
/// 状态、总评分、通关日期仍在按账号存的 <see cref="LibraryGameMeta"/> 里。
/// </summary>
public class GameJourney
{
    public int Id { get; init; }

    public long AppId { get; init; }

    public long? StartedAt { get; set; }

    /// <summary>story / storyPlus / achievements / playtime / custom / none</summary>
    public string? GoalKind { get; set; }

    public int? GoalHours { get; set; }

    public int? GoalAchievementPercent { get; set; }

    public string? GoalNote { get; set; }

    /// <summary>里程碑清单（JSON 数组：id、title、doneAt）。</summary>
    public string? Milestones { get; set; }

    /// <summary>beaten / goalReached / perfect</summary>
    public string? CompletionLevel { get; set; }

    /// <summary>noTime / tooHard / waitingUpdate / waitingFriends / burnedOut / other</summary>
    public string? ShelvedReason { get; set; }

    public long? ResumeAt { get; set; }

    public string? ResumeNote { get; set; }

    /// <summary>「下一个」队列里的顺序，越小越靠前；不在队列里为空。</summary>
    public double? QueueOrder { get; set; }

    /// <summary>分项评分（JSON 对象：story / gameplay / visuals / audio / replay，1~10）。</summary>
    public string? ReviewScores { get; set; }

    public string? ReviewSummary { get; set; }

    public bool? Recommend { get; set; }

    public bool? WouldReplay { get; set; }

    public long UpdatedAt { get; set; }
}

/// <summary>游戏心得：随笔、搁置记录、里程碑、完成总结、回坑记录。</summary>
public class GameJournalEntry
{
    public int Id { get; init; }

    public string? SyncUid { get; set; }

    public long AppId { get; set; }

    /// <summary>note / shelve / milestone / review / resume</summary>
    public string Kind { get; set; } = "note";

    public string Body { get; set; } = string.Empty;

    public bool Spoiler { get; set; }

    public string? Mood { get; set; }

    public long CreatedAt { get; set; }

    public long UpdatedAt { get; set; }
}

/// <summary>周目：一次从开坑到结束的游玩，二周目单独一条。</summary>
public class GamePlaythrough
{
    public int Id { get; init; }

    public string? SyncUid { get; set; }

    public long AppId { get; set; }

    public string? Label { get; set; }

    public long? StartedAt { get; set; }

    public long? EndedAt { get; set; }

    /// <summary>ongoing / completed / abandoned</summary>
    public string Outcome { get; set; } = "ongoing";

    public string? CompletionLevel { get; set; }

    public int? PlaytimeMinutes { get; set; }

    public int? Rating { get; set; }

    public string? Note { get; set; }

    public long CreatedAt { get; set; }

    public long UpdatedAt { get; set; }
}

/// <summary>用户自定义的游戏分类标签。</summary>
public class GameTag
{
    public int Id { get; init; }

    public string? SyncUid { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Color { get; set; } = "slate";

    public string? Icon { get; set; }

    public int SortOrder { get; set; }

    public long CreatedAt { get; set; }

    public long UpdatedAt { get; set; }
}

/// <summary>标签和游戏的关联。引用标签的同步 ID，跨设备时才对得上。</summary>
public class GameTagLink
{
    public int Id { get; init; }

    public string TagUid { get; set; } = string.Empty;

    public long AppId { get; set; }

    public long CreatedAt { get; set; }
}

/// <summary>游玩计划。日期是逻辑日，存「距 1970-01-01 的天数」。</summary>
public class PlayPlan
{
    public int Id { get; init; }

    public string? SyncUid { get; set; }

    public long? AppId { get; set; }

    public string? Title { get; set; }

    /// <summary>day 某一天 / range 一段时间 / bucket 粗略时段（本周、本月……）</summary>
    public string Kind { get; set; } = "day";

    public int StartDay { get; set; }

    public int? EndDay { get; set; }

    /// <summary>week / month / quarter / year，只有 bucket 有。</summary>
    public string? Bucket { get; set; }

    public int? EstimatedMinutes { get; set; }

    /// <summary>pending / done / skipped</summary>
    public string Status { get; set; } = "pending";

    public string? Note { get; set; }

    public long CreatedAt { get; set; }

    public long UpdatedAt { get; set; }
}

/// <summary>年度挑战：自愿发起，没完成没有惩罚。</summary>
public class PlayChallenge
{
    public int Id { get; init; }

    public string? SyncUid { get; set; }

    public int Year { get; set; }

    /// <summary>completeGames / clearBacklog / startNew / perfectGames / playtimeHours</summary>
    public string Kind { get; set; } = "completeGames";

    public int Target { get; set; }

    public string? Title { get; set; }

    public long CreatedAt { get; set; }

    public long UpdatedAt { get; set; }
}
