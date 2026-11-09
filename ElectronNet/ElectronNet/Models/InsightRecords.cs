namespace ElectronNet.Models;

/// <summary>本机观测到的游戏版本变化（ACF buildid）。第一次看到一款游戏只是基线。</summary>
public class AppBuildRecord
{
    public int Id { get; init; }

    public int AppId { get; init; }

    public int BuildId { get; init; }

    /// <summary>版本出现的时间（Unix 秒）。优先用 ACF 的 LastUpdated。</summary>
    public long ObservedAt { get; init; }

    public bool IsBaseline { get; init; }
}

/// <summary>一款游戏在商店上的公开状态。美区标价，每周检查一次。</summary>
public class AppStoreStatus
{
    public int AppId { get; init; }

    public bool Visible { get; set; }

    public bool Purchasable { get; set; }

    public bool IsFree { get; set; }

    public int? PriceCents { get; set; }

    public int? ReviewCount { get; set; }

    public int? ReviewPercent { get; set; }

    public string? Franchise { get; set; }

    public long? ReleaseDate { get; set; }

    /// <summary>第一次发现下架的时间。重新上架后清空。</summary>
    public long? DelistedAt { get; set; }

    /// <summary>商店标签 ID（JSON 数组，按票数排序）。购前模拟用它比较相似度。</summary>
    public string? TagIds { get; set; }

    /// <summary>商店分类 ID（JSON 数组）：单人、多人、在线合作等。</summary>
    public string? CategoryIds { get; set; }

    public long CheckedAt { get; set; }
}

/// <summary>一期周刊。Payload 是生成时冻结的完整内容，之后只有 ReadAt 会变。</summary>
public class WeeklyIssue
{
    public int Id { get; init; }

    /// <summary>weekly / extra</summary>
    public string Kind { get; init; } = "weekly";

    /// <summary>同一期只出一次：周刊是周起始日，号外是触发事件的键。</summary>
    public string IssueKey { get; init; } = string.Empty;

    public int Number { get; init; }

    public string FromDay { get; init; } = string.Empty;

    public string ToDay { get; init; } = string.Empty;

    public long GeneratedAt { get; init; }

    public long? ReadAt { get; set; }

    public bool Backfilled { get; init; }

    public string Payload { get; init; } = "{}";
}
