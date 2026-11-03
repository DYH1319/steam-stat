namespace SteamStat.Contracts.Ipc;

// ---------------------------------------------------------------------------
// 截图档案：索引 Steam 截图目录（userdata/{accountId}/760/remote/{appId}/screenshots）
// ---------------------------------------------------------------------------

public sealed record ScreenshotIndexResultDto
{
    public required bool Success { get; init; }
    public required int Total { get; init; }
    public required int Added { get; init; }
    public required int Updated { get; init; }
    /// <summary>本次扫描中磁盘上已不存在的截图数（记录保留，标记为缺失）。</summary>
    public required int Missing { get; init; }
    [IpcOptional] public string? Error { get; init; }
}

public sealed record ScreenshotQueryRequest
{
    [IpcOptional, IpcRange(0, int.MaxValue)] public int? AppId { get; init; }
    [IpcOptional, IpcMaxLength(32)] public string? SteamId { get; init; }
    [IpcOptional, IpcNumber] public long? From { get; init; }
    [IpcOptional, IpcNumber] public long? To { get; init; }
    /// <summary>是否包含磁盘上已不存在的截图记录，默认不包含。</summary>
    [IpcOptional] public bool? IncludeMissing { get; init; }
    [IpcRange(0, int.MaxValue)] public required int Offset { get; init; }
    [IpcRange(1, 5000)] public required int Limit { get; init; }
}

public sealed record ScreenshotDto
{
    public required int Id { get; init; }
    public required int AppId { get; init; }
    public required string SteamId { get; init; }
    public required string FileName { get; init; }
    /// <summary>原图绝对路径（渲染层经 steam-stat-file 协议显示）。</summary>
    public required string FilePath { get; init; }
    [IpcOptional] public string? ThumbnailPath { get; init; }
    [IpcNumber] public required long TakenAt { get; init; }
    [IpcNumber] public required long FileSize { get; init; }
    [IpcOptional] public int? Width { get; init; }
    [IpcOptional] public int? Height { get; init; }
    [IpcOptional] public string? Caption { get; init; }
    public required bool Missing { get; init; }
}

public sealed record ScreenshotPageDto
{
    public required int Total { get; init; }
    public required IReadOnlyList<ScreenshotDto> Items { get; init; }
    [IpcOptional, IpcNumber] public int? NextOffset { get; init; }
    [IpcOptional, IpcNumber] public long? IndexedAt { get; init; }
}

public sealed record ScreenshotAppDto
{
    public required int AppId { get; init; }
    public required string AppName { get; init; }
    public required int Count { get; init; }
    [IpcNumber] public required long FirstTakenAt { get; init; }
    [IpcNumber] public required long LastTakenAt { get; init; }
    [IpcNumber] public required long TotalSize { get; init; }
}

public sealed record ScreenshotActionRequest
{
    [IpcRange(1, int.MaxValue)] public required int Id { get; init; }
    [IpcStringValues("open", "reveal")] public required string Action { get; init; }
}

// ---------------------------------------------------------------------------
// 存档备份：只读快照 + 版本历史（内容寻址去重，绝不写回原目录）
// ---------------------------------------------------------------------------

public sealed record SaveBackupTargetDto
{
    public required int Id { get; init; }
    public required int AppId { get; init; }
    public required string AppName { get; init; }
    /// <summary>steamCloud = userdata/{id}/{appId}/remote；autoCloud = 由 remotecache.vdf 推断；custom = 用户手动添加。</summary>
    [IpcStringValues("steamCloud", "autoCloud", "custom")] public required string Kind { get; init; }
    public required string Path { get; init; }
    [IpcOptional] public string? SteamId { get; init; }
    [IpcOptional] public string? Label { get; init; }
    public required bool Enabled { get; init; }
    public required bool Exists { get; init; }
    [IpcNumber] public required long CreatedAt { get; init; }
    [IpcOptional, IpcNumber] public long? LastScanAt { get; init; }
    [IpcOptional, IpcNumber] public long? LastSnapshotAt { get; init; }
    [IpcOptional] public string? LastError { get; init; }
    public required int SnapshotCount { get; init; }
    [IpcNumber] public required long LatestSize { get; init; }
    public required int LatestFileCount { get; init; }
    /// <summary>该目录全部版本在备份库中实际占用（去重后）的字节数。</summary>
    [IpcNumber] public required long StoredSize { get; init; }
}

public sealed record SaveBackupSuggestionDto
{
    public required int AppId { get; init; }
    public required string AppName { get; init; }
    [IpcStringValues("steamCloud", "autoCloud")] public required string Kind { get; init; }
    public required string Path { get; init; }
    [IpcOptional] public string? SteamId { get; init; }
    public required int FileCount { get; init; }
}

public sealed record SaveBackupDiscoverResultDto
{
    /// <summary>候选目录（Steam 云存档与 Auto-Cloud 推断），均需用户确认后加入。</summary>
    public required IReadOnlyList<SaveBackupSuggestionDto> Suggestions { get; init; }
}

public sealed record SaveBackupTargetAddRequest
{
    [IpcRange(0, int.MaxValue)] public required int AppId { get; init; }
    [IpcMaxLength(1024)] public required string Path { get; init; }
    [IpcOptional, IpcStringValues("steamCloud", "autoCloud", "custom")] public string? Kind { get; init; }
    [IpcOptional, IpcMaxLength(32)] public string? SteamId { get; init; }
    [IpcOptional, IpcMaxLength(128)] public string? Label { get; init; }
}

public sealed record SaveBackupTargetAddResultDto
{
    public required bool Success { get; init; }
    [IpcOptional, IpcStringValues("notFound", "notDirectory", "duplicate", "forbidden", "failed")]
    public string? Error { get; init; }
    [IpcOptional] public SaveBackupTargetDto? Target { get; init; }
}

public sealed record SaveBackupTargetUpdateRequest
{
    [IpcRange(1, int.MaxValue)] public required int Id { get; init; }
    [IpcOptional] public bool? Enabled { get; init; }
    [IpcOptional, IpcMaxLength(128)] public string? Label { get; init; }
}

public sealed record SaveBackupTargetRemoveRequest
{
    [IpcRange(1, int.MaxValue)] public required int Id { get; init; }
    /// <summary>同时删除该目录的全部历史版本（默认保留）。</summary>
    [IpcOptional] public bool? DeleteSnapshots { get; init; }
}

public sealed record SaveBackupTargetIdRequest
{
    [IpcRange(1, int.MaxValue)] public required int TargetId { get; init; }
}

public sealed record SaveBackupRunRequest
{
    /// <summary>为空时快照全部启用的目录。</summary>
    [IpcOptional, IpcRange(1, int.MaxValue)] public IReadOnlyList<int>? TargetIds { get; init; }
}

public sealed record SaveBackupStatusDto
{
    [IpcStringValues("idle", "running", "completed", "cancelled", "failed")] public required string State { get; init; }
    public required int Total { get; init; }
    public required int Done { get; init; }
    public required int Failed { get; init; }
    /// <summary>本次新产生的版本数（内容未变化的目录不会产生新版本）。</summary>
    public required int Created { get; init; }
    [IpcOptional] public int? CurrentTargetId { get; init; }
    [IpcOptional] public string? CurrentLabel { get; init; }
    [IpcOptional, IpcStringValues("manual", "schedule", "sessionEnd")] public string? Trigger { get; init; }
    [IpcOptional, IpcNumber] public long? StartedAt { get; init; }
    [IpcOptional, IpcNumber] public long? FinishedAt { get; init; }
    [IpcOptional] public string? Error { get; init; }
}

public sealed record SaveBackupSnapshotDto
{
    public required int Id { get; init; }
    public required int TargetId { get; init; }
    [IpcNumber] public required long CreatedAt { get; init; }
    public required int FileCount { get; init; }
    [IpcNumber] public required long TotalSize { get; init; }
    /// <summary>本版本新写入备份库的字节数（去重后）。</summary>
    [IpcNumber] public required long AddedSize { get; init; }
    public required int ChangedFiles { get; init; }
    [IpcStringValues("manual", "schedule", "sessionEnd")] public required string Trigger { get; init; }
    /// <summary>最近修改的存档文件时间（近似游戏写入存档的时间）。</summary>
    [IpcOptional, IpcNumber] public long? LatestModifiedAt { get; init; }
}

public sealed record SaveBackupSnapshotIdRequest
{
    [IpcRange(1, int.MaxValue)] public required int SnapshotId { get; init; }
}

public sealed record SaveBackupFileDto
{
    public required string RelativePath { get; init; }
    [IpcNumber] public required long Size { get; init; }
    [IpcNumber] public required long ModifiedAt { get; init; }
    public required string Sha256 { get; init; }
    [IpcStringValues("added", "modified", "unchanged")] public required string Change { get; init; }
}

public sealed record SaveBackupSnapshotFilesDto
{
    public required int SnapshotId { get; init; }
    [IpcOptional] public int? PreviousSnapshotId { get; init; }
    public required IReadOnlyList<SaveBackupFileDto> Items { get; init; }
    /// <summary>相比上一版本被删除的文件。</summary>
    public required IReadOnlyList<string> Removed { get; init; }
}

public sealed record SaveBackupExportResultDto
{
    public required bool Success { get; init; }
    [IpcOptional] public string? Path { get; init; }
    [IpcOptional] public string? Error { get; init; }
}

// ---------------------------------------------------------------------------
// 自己的资料变化：昵称 / 头像 / 等级 / 展示徽章的历史
// ---------------------------------------------------------------------------

public sealed record ProfileHistoryQueryRequest
{
    [IpcOptional, IpcMaxLength(32)] public string? SteamId { get; init; }
}

public sealed record ProfileHistoryEntryDto
{
    public required int Id { get; init; }
    public required string SteamId { get; init; }
    [IpcStringValues("personaName", "avatar", "level", "favoriteBadge")] public required string Field { get; init; }
    /// <summary>favoriteBadge 为 JSON（badgeId / level / appId / communityItemId），avatar 为头像哈希。</summary>
    public required string Value { get; init; }
    [IpcOptional] public string? PreviousValue { get; init; }
    [IpcNumber] public required long ObservedAt { get; init; }
    [IpcStringValues("cm", "friends", "local")] public required string Source { get; init; }
    /// <summary>头像历史在本地的存档副本（下载失败时为空，渲染层回退到 CDN）。</summary>
    [IpcOptional] public string? LocalPath { get; init; }
}

public sealed record ProfileHistoryCaptureResultDto
{
    public required int Accounts { get; init; }
    public required int Recorded { get; init; }
    [IpcOptional] public string? Error { get; init; }
}
