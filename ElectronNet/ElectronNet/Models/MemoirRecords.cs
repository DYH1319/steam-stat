namespace ElectronNet.Models;

/// <summary>用户标注的人生事件。日期是逻辑日，存「距 1970-01-01 的天数」。</summary>
public class LifeEvent
{
    public int Id { get; init; }

    public string Title { get; set; } = string.Empty;

    public string? Note { get; set; }

    /// <summary>study / work / move / travel / family / relationship / health / milestone / other</summary>
    public string Kind { get; set; } = "other";

    public int StartDay { get; set; }

    public int? EndDay { get; set; }

    /// <summary>从这里开始新的一章。</summary>
    public bool Boundary { get; set; }

    public long CreatedAt { get; init; }

    public long UpdatedAt { get; set; }

    /// <summary>多设备同步的全局唯一 ID（插入时由触发器生成）。</summary>
    public string? SyncUid { get; set; }
}

/// <summary>时光胶囊。正文在到期前不发给渲染层。</summary>
public class TimeCapsule
{
    public int Id { get; init; }

    public string? Label { get; set; }

    public string Message { get; set; } = string.Empty;

    public long CreatedAt { get; init; }

    public long OpenAt { get; set; }

    public int? AppId { get; set; }

    /// <summary>条件满足的时间。之后正文才能读。</summary>
    public long? ReadyAt { get; set; }

    public long? OpenedAt { get; set; }

    /// <summary>封存时的档案摘要（JSON）。</summary>
    public string? Context { get; set; }

    public long UpdatedAt { get; set; }

    public string? SyncUid { get; set; }
}

/// <summary>Steam 年度回顾的原始回复。解析器升级后用原始字节重新解析。</summary>
public class YearInReviewRecord
{
    public int Id { get; init; }

    public string SteamId { get; init; } = string.Empty;

    public int Year { get; init; }

    public long FetchedAt { get; set; }

    /// <summary>EResult 的名字。不是 OK 时没有 Payload。</summary>
    public string Result { get; set; } = string.Empty;

    public byte[]? Payload { get; set; }
}

/// <summary>
/// 云存档写入时间（remotecache.vdf）。每个文件只留最后一次写入，所以每次看到新的时间都记一条，
/// 安装之前的那些时间是重建史前游玩日历的痕迹。不记路径，只记路径的哈希。
/// </summary>
public class CloudSaveTrace
{
    public int Id { get; init; }

    public string SteamId { get; init; } = string.Empty;

    public int AppId { get; init; }

    public string FileKey { get; init; } = string.Empty;

    public long WrittenAt { get; init; }

    public long ObservedAt { get; init; }
}
