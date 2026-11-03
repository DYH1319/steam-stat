namespace ElectronNet.Models;

/// <summary>
/// 截图档案：Steam 截图目录（userdata/{accountId}/760/remote/{appId}/screenshots）的索引。
/// 只记录路径与元数据，不复制图片；文件被删除后保留记录并标记 MissingSince。
/// </summary>
public class ScreenshotArchive
{
    public int Id { get; init; }

    public long AccountId { get; init; }

    public string SteamId { get; set; } = string.Empty;

    public long AppId { get; init; }

    public string FileName { get; init; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    public string? ThumbnailPath { get; set; }

    public long TakenAt { get; set; }

    public long FileSize { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public string? Caption { get; set; }

    public long IndexedAt { get; set; }

    public long? MissingSince { get; set; }
}

/// <summary>
/// 存档备份目录：Steam 云存档目录、由 remotecache.vdf 推断的 Auto-Cloud 目录或用户手动添加的目录。
/// </summary>
public class SaveBackupTarget
{
    public int Id { get; init; }

    public long AppId { get; set; }

    /// <summary>steamCloud / autoCloud / custom</summary>
    public string Kind { get; init; } = "custom";

    public string Path { get; init; } = string.Empty;

    public string? SteamId { get; set; }

    public string? Label { get; set; }

    public bool Enabled { get; set; } = true;

    public long CreatedAt { get; init; }

    public long? LastScanAt { get; set; }

    public long? LastSnapshotAt { get; set; }

    public string? LastError { get; set; }
}

/// <summary>
/// 存档的一个版本：内容变化时才产生；文件内容按 SHA-256 存入备份库（去重）。
/// </summary>
public class SaveBackupSnapshot
{
    public int Id { get; init; }

    public int TargetId { get; init; }

    public long CreatedAt { get; init; }

    public int FileCount { get; init; }

    public long TotalSize { get; init; }

    public long AddedSize { get; init; }

    public int ChangedFiles { get; init; }

    /// <summary>manual / schedule / sessionEnd</summary>
    public string Trigger { get; init; } = "manual";

    /// <summary>清单指纹（相对路径 + 哈希），用于判断内容是否变化。</summary>
    public string ManifestHash { get; init; } = string.Empty;

    public long? LatestModifiedAt { get; init; }
}

/// <summary>
/// 版本清单中的一个文件。
/// </summary>
public class SaveBackupFile
{
    public int Id { get; init; }

    public int SnapshotId { get; init; }

    public string RelativePath { get; init; } = string.Empty;

    public long Size { get; init; }

    public long ModifiedAt { get; init; }

    public string Sha256 { get; init; } = string.Empty;
}

/// <summary>
/// 自己的资料变化记录（字段级）：值与上一条不同时才写入，首次观测作为基线。
/// </summary>
public class ProfileHistoryEntry
{
    public int Id { get; init; }

    public string SteamId { get; init; } = string.Empty;

    /// <summary>personaName / avatar / level / favoriteBadge</summary>
    public string Field { get; init; } = string.Empty;

    public string Value { get; init; } = string.Empty;

    public string? PreviousValue { get; init; }

    public long ObservedAt { get; init; }

    /// <summary>cm / friends / local</summary>
    public string Source { get; init; } = string.Empty;

    public string? LocalPath { get; set; }
}
