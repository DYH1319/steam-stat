namespace ElectronNet.Models;

/// <summary>
/// 永久归档的已解锁成就（按登录账号区分；只增不删，Steam 侧重置也保留历史）
/// </summary>
public class AchievementUnlockArchive
{
    public int Id { get; init; }

    public string AccountName { get; init; } = string.Empty;

    public string SteamId { get; set; } = string.Empty;

    public long AppId { get; init; }

    public string InternalName { get; init; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Icon { get; set; } = string.Empty;

    public bool Hidden { get; set; }

    public double? GlobalPercent { get; set; }

    public long UnlockTime { get; set; }

    public long RecordedAt { get; init; }
}

/// <summary>
/// 每个游戏最近一次归档时的成就总数 / 已解锁数（离线也能统计完成度）
/// </summary>
public class AchievementGameArchive
{
    public int Id { get; init; }

    public string AccountName { get; init; } = string.Empty;

    public string SteamId { get; set; } = string.Empty;

    public long AppId { get; init; }

    public string AppName { get; set; } = string.Empty;

    public int Total { get; set; }

    public int Unlocked { get; set; }

    /// <summary>
    /// 归档时概览（Player.GetAchievementsProgress）给出的总数。概览与详情（成就定义 + 解锁匹配）口径可能不同，
    /// 增量同步只用它判断「是否有更新」，避免两者永远对不上导致反复提示待同步。旧数据为 null。
    /// </summary>
    public int? ProgressTotal { get; set; }

    /// <summary>
    /// 归档时概览给出的已解锁数（见 <see cref="ProgressTotal"/>）。
    /// </summary>
    public int? ProgressUnlocked { get; set; }

    public long SyncedAt { get; set; }
}

/// <summary>
/// 成就目标清单：用户想拿的成就（快照名称 / 图标，便于离线展示）
/// </summary>
public class AchievementTarget
{
    public int Id { get; init; }

    public string AccountName { get; init; } = string.Empty;

    public long AppId { get; init; }

    public string AppName { get; set; } = string.Empty;

    public string InternalName { get; init; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Icon { get; set; } = string.Empty;

    public string? Note { get; set; }

    public long CreatedAt { get; init; }
}
