namespace ElectronNet.Models;

/// <summary>多设备同步的本机状态（单行，id = 1）。</summary>
public class SyncControl
{
    public int Id { get; init; } = 1;

    /// <summary>为 true 时变更触发器才会写 outbox。</summary>
    public bool Enabled { get; set; }

    /// <summary>应用远端变更的事务内临时置为 true，触发器据此不把远端变更再记进 outbox。</summary>
    public bool Applying { get; set; }

    public string? VaultId { get; set; }

    /// <summary>本机已写出的最大段序号。</summary>
    public long DeviceSeq { get; set; }

    /// <summary>本机全量快照是否已上传（首次同步与恢复备份后为 false）。</summary>
    public bool FullExportDone { get; set; }

    public long? LastSyncAt { get; set; }

    public string? LastError { get; set; }
}

/// <summary>本机待上传的行变更（由 SQLite 触发器写入）。</summary>
public class SyncOutboxEntry
{
    public long Seq { get; init; }

    public string TableName { get; init; } = string.Empty;

    /// <summary>变更行的 rowid（upsert 时在上传前按 rowid 读取当前值）。</summary>
    public long RowId { get; init; }

    /// <summary>删除时记下的同步键（行已不存在，无法再读取）。</summary>
    public string? SyncKey { get; init; }

    /// <summary>upsert | delete</summary>
    public string Op { get; init; } = "upsert";

    /// <summary>变更时间（Unix 毫秒）。</summary>
    public long ChangedAt { get; init; }
}

/// <summary>已应用到本机的远端设备进度。</summary>
public class SyncCursor
{
    public string DeviceId { get; init; } = string.Empty;

    public long LastSeq { get; set; }

    public string? DeviceName { get; set; }

    public string? AppVersion { get; set; }

    public string? Platform { get; set; }

    /// <summary>远端设备最近一次上传的时间（Unix 秒）。</summary>
    public long? RemoteUpdatedAt { get; set; }

    public long? AppliedAt { get; set; }
}

/// <summary>每个同步行的版本（最后写入胜出），删除后保留为墓碑。</summary>
public class SyncRowVersion
{
    public string TableName { get; init; } = string.Empty;

    public string SyncKey { get; init; } = string.Empty;

    /// <summary>版本时间（Unix 毫秒）。</summary>
    public long ChangedAt { get; set; }

    /// <summary>写出该版本的设备。</summary>
    public string DeviceId { get; set; } = string.Empty;

    public bool Deleted { get; set; }
}
