namespace ElectronNet.Models;

public class UseAppRecord
{
    public int Id { get; init; }

    public int AppId { get; init; }

    public string SteamId { get; init; } = string.Empty;

    public int StartTime { get; set; }

    public int? EndTime { get; set; }

    public int? Duration { get; set; }

    public int? LastHeartbeat { get; set; }

    public long IdleDuration { get; set; }

    public long? ForegroundDuration { get; set; }

    public string? DeviceId { get; set; }

    public string Source { get; set; } = "auto";

    public string? EndReason { get; set; }

    public string? Note { get; set; }

    public string? Tags { get; set; }

    public int? UpdatedAt { get; set; }

    public int? ImportBatchId { get; set; }

    /// <summary>多设备同步的全局唯一 ID（插入时由触发器生成）。</summary>
    public string? SyncUid { get; set; }

    /// <summary>会话自动标签（SessionTags 位标记）。为空表示还没打标。每台设备自己算，不参与同步。</summary>
    public int? AutoTags { get; set; }

    /// <summary>开始时本机观测到的游戏版本。</summary>
    public int? BuildId { get; set; }
}