namespace SteamStat.Contracts.Ipc;

// ---------------------------------------------------------------------------
// 数据导出：表格（CSV / xlsx）与完整 JSON。保存位置由 Host 弹出系统对话框选择。
// ---------------------------------------------------------------------------

public sealed record DataExportRequest
{
    [IpcStringValues("csv", "xlsx", "json")] public required string Format { get; init; }

    /// <summary>
    /// 表格导出的数据集：sessions / friendRecords / achievementUnlocks / achievementGames / achievementTargets /
    /// library / profileHistory / playtimeSnapshots / screenshots。完整 JSON 忽略此项，总是导出全部档案表。
    /// </summary>
    [IpcOptional] public IReadOnlyList<string>? Datasets { get; init; }

    /// <summary>时间范围（Unix 秒，闭区间），只作用于带时间维度的数据集。</summary>
    [IpcOptional, IpcNumber] public long? From { get; init; }
    [IpcOptional, IpcNumber] public long? To { get; init; }
}

public sealed record DataExportTableDto
{
    public required string Id { get; init; }
    public required int Rows { get; init; }
    /// <summary>超出格式上限被截断（xlsx 单表最多 1,048,575 行数据）。</summary>
    public required bool Truncated { get; init; }
}

public sealed record DataExportResultDto
{
    [IpcStringValues("completed", "cancelled", "failed")] public required string Status { get; init; }
    /// <summary>用于「在文件夹中显示」的标识（Host 签发，不是路径）。</summary>
    [IpcOptional] public string? ExportId { get; init; }
    [IpcOptional] public string? Path { get; init; }
    [IpcNumber] public required long SizeBytes { get; init; }
    public required IReadOnlyList<DataExportTableDto> Tables { get; init; }
    [IpcOptional] public string? Error { get; init; }
}

public sealed record DataExportRevealRequest
{
    [IpcMaxLength(64)] public required string ExportId { get; init; }
}

// ---------------------------------------------------------------------------
// 手动备份与恢复：.ssbackup（zip，可用口令加密）。恢复在下次启动时应用，应用前自动保留当前数据库。
// 口令经渲染层编码为「s1:」+ Base64(UTF-8)，避免被 Electron.NET 桥改写。
// ---------------------------------------------------------------------------

public sealed record BackupCreateRequest
{
    /// <summary>留空表示不加密。</summary>
    [IpcOptional, IpcMaxLength(1024)] public string? Password { get; init; }
    public required bool IncludeSaveBackups { get; init; }
    /// <summary>渲染层持久化偏好（localStorage 快照，JSON 文本）。</summary>
    [IpcOptional, IpcMaxLength(900_000)] public string? RendererState { get; init; }
}

public sealed record BackupCreateResultDto
{
    [IpcStringValues("completed", "cancelled", "failed")] public required string Status { get; init; }
    [IpcOptional] public string? ExportId { get; init; }
    [IpcOptional] public string? Path { get; init; }
    [IpcNumber] public required long SizeBytes { get; init; }
    public required bool Encrypted { get; init; }
    [IpcOptional] public BackupManifestDto? Manifest { get; init; }
    [IpcOptional] public string? Error { get; init; }
}

public sealed record BackupCountDto
{
    public required string Key { get; init; }
    [IpcNumber] public required long Count { get; init; }
}

public sealed record BackupManifestDto
{
    public required string AppVersion { get; init; }
    [IpcNumber] public required long CreatedAt { get; init; }
    [IpcOptional] public string? DeviceName { get; init; }
    /// <summary>数据库来自本机当前设备。</summary>
    public required bool SameDevice { get; init; }
    [IpcNumber] public required long DatabaseBytes { get; init; }
    public required bool HasSettings { get; init; }
    public required bool HasRendererState { get; init; }
    public required int SaveObjectCount { get; init; }
    [IpcNumber] public required long SaveObjectBytes { get; init; }
    public required IReadOnlyList<BackupCountDto> Counts { get; init; }
}

public sealed record BackupInspectResultDto
{
    /// <summary>ready = 已读出清单，可以恢复；locked = 已加密，需要口令。</summary>
    [IpcStringValues("ready", "locked", "cancelled", "failed")] public required string Status { get; init; }
    [IpcOptional] public string? Token { get; init; }
    [IpcOptional] public string? FileName { get; init; }
    [IpcNumber] public required long SizeBytes { get; init; }
    public required bool Encrypted { get; init; }
    [IpcOptional] public BackupManifestDto? Manifest { get; init; }
    /// <summary>invalid / wrongPassword / newerVersion / io / failed</summary>
    [IpcOptional] public string? Error { get; init; }
}

public sealed record BackupUnlockRequest
{
    [IpcMaxLength(64)] public required string Token { get; init; }
    [IpcMaxLength(1024)] public required string Password { get; init; }
}

public sealed record BackupRestoreRequest
{
    [IpcMaxLength(64)] public required string Token { get; init; }
    public required bool RestoreSettings { get; init; }
    public required bool RestoreRendererState { get; init; }
    public required bool RestoreSaveBackups { get; init; }
}

public sealed record BackupRestoreResultDto
{
    public required bool Success { get; init; }
    /// <summary>invalid / corrupt / newerVersion / io / failed</summary>
    [IpcOptional] public string? Error { get; init; }
}

public sealed record BackupRestoreStatusDto
{
    /// <summary>已暂存、等待下次启动应用的恢复。</summary>
    public required bool Pending { get; init; }
    [IpcOptional] public string? PendingSource { get; init; }
    [IpcOptional, IpcNumber] public long? LastAppliedAt { get; init; }
    [IpcOptional] public string? LastSource { get; init; }
    [IpcOptional] public bool? LastSuccess { get; init; }
    [IpcOptional] public string? LastError { get; init; }
}

public sealed record BackupRendererStateDto
{
    /// <summary>恢复的渲染层偏好（只返回一次）。</summary>
    [IpcOptional] public string? State { get; init; }
}

public sealed record AppRelaunchResultDto
{
    /// <summary>false 表示当前运行方式不支持自动重启（开发模式），应用会直接退出。</summary>
    public required bool Relaunched { get; init; }
}
