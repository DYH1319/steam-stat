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

    /// <summary>所在成就集。旧数据和没有分组的 schema 为 null。</summary>
    public long? GroupId { get; set; }

    /// <summary>成就或所在的集已被开发者归档，别人再也拿不到。</summary>
    public bool Archived { get; set; }
}

/// <summary>
/// 每个游戏最近一次归档时的成就总数 / 已解锁数（离线也能统计完成度）。
/// Total / Unlocked 是 Steam 口径：基础集加已拥有的 DLC 集，未获得的归档成就不计。
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

    /// <summary>全部成就数，含未拥有的 DLC 和归档成就。null 表示这行是成就集适配之前写的，Total 还是旧口径。</summary>
    public int? AllTotal { get; set; }

    /// <summary>全部成就里的已解锁数（见 <see cref="AllTotal"/>）。</summary>
    public int? AllUnlocked { get; set; }

    public long SyncedAt { get; set; }
}

/// <summary>
/// 每个账号每个成就集的进度。Completed 一旦为 true 就不再清除：集完成后再加成就，完成状态保留。
/// </summary>
public class AchievementSetArchive
{
    public int Id { get; init; }

    public string AccountName { get; init; } = string.Empty;

    public string SteamId { get; set; } = string.Empty;

    public long AppId { get; init; }

    public long GroupId { get; init; }

    public string Name { get; set; } = string.Empty;

    public long? DlcAppId { get; set; }

    public bool Archived { get; set; }

    /// <summary>是否计入完成度。</summary>
    public bool Counted { get; set; }

    /// <summary>DLC 是否拥有。基础集和无法判断时为 null。</summary>
    public bool? Owned { get; set; }

    public int Total { get; set; }

    public int Unlocked { get; set; }

    public int AllTotal { get; set; }

    public bool Completed { get; set; }

    /// <summary>集内最后一个解锁时间，所以历史也能补。</summary>
    public long? CompletedAt { get; set; }

    public long SyncedAt { get; set; }
}

/// <summary>
/// 最近一次观测到的成就 schema，只用来和下一次比较，不参与同步：每台设备有自己的基线。
/// </summary>
public class AchievementSchemaState
{
    public long AppId { get; init; }

    public long SchemaHash { get; set; }

    public string Language { get; set; } = string.Empty;

    /// <summary>JSON：每个成就的内部名、名称、所在集、是否归档。</summary>
    public string Items { get; set; } = string.Empty;

    public long ObservedAt { get; set; }
}

/// <summary>
/// 成就 schema 的变化。同一次变化在多台设备上 schema 哈希相同（哈希不随语言变化），靠这个去重。
/// </summary>
public class AchievementSchemaChange
{
    public int Id { get; init; }

    public long AppId { get; init; }

    public long SchemaHash { get; init; }

    public string InternalName { get; init; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>added / removed / archived / restored / moved / renamed</summary>
    public string Kind { get; init; } = string.Empty;

    public string? OldValue { get; set; }

    public string? NewValue { get; set; }

    public long DetectedAt { get; init; }
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
