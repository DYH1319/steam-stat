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

// ---------------------------------------------------------------------------
// 多设备同步：自带存储（本地文件夹 / WebDAV / S3），端到端加密。
// 密码、密钥与同步口令经渲染层编码为「s1:」+ Base64；Host 不会把它们回传给渲染层。
// ---------------------------------------------------------------------------

public sealed record SyncConfigDto
{
    /// <summary>folder | webdav | s3（cloud = 官方托管云，尚未推出）。</summary>
    [IpcStringValues("folder", "webdav", "s3")] public required string Backend { get; init; }
    [IpcOptional, IpcMaxLength(1024)] public string? FolderPath { get; init; }
    [IpcOptional, IpcMaxLength(2048)] public string? WebDavUrl { get; init; }
    [IpcOptional, IpcMaxLength(256)] public string? WebDavUser { get; init; }
    [IpcOptional, IpcMaxLength(2048)] public string? S3Endpoint { get; init; }
    [IpcOptional, IpcMaxLength(64)] public string? S3Region { get; init; }
    [IpcOptional, IpcMaxLength(128)] public string? S3Bucket { get; init; }
    [IpcOptional, IpcMaxLength(256)] public string? S3AccessKeyId { get; init; }
    [IpcOptional] public bool? S3PathStyle { get; init; }
    /// <summary>远端目录（默认 steam-stat-sync）。</summary>
    [IpcOptional, IpcMaxLength(256)] public string? RemoteRoot { get; init; }
    [IpcRange(5, 1440)] public required int IntervalMinutes { get; init; }
    /// <summary>按间隔自动同步（关闭后只在手动点击时同步，变更仍会记录）。</summary>
    [IpcOptional] public bool? AutoSync { get; init; }
}

public sealed record SyncConnectRequest
{
    public required SyncConfigDto Config { get; init; }
    /// <summary>留空表示沿用已保存的密码。</summary>
    [IpcOptional, IpcMaxLength(4096)] public string? WebDavPassword { get; init; }
    [IpcOptional, IpcMaxLength(4096)] public string? S3SecretAccessKey { get; init; }
}

public sealed record SyncTestResultDto
{
    public required bool Success { get; init; }
    /// <summary>unauthorized / network / configuration / clockSkew / protocol / invalid / failed</summary>
    [IpcOptional] public string? Error { get; init; }
    /// <summary>远端已经有同步数据（加入已有同步，需要输入原来的口令）。</summary>
    public required bool VaultExists { get; init; }
    public required int DeviceCount { get; init; }
}

public sealed record SyncSetupRequest
{
    public required SyncConnectRequest Connection { get; init; }
    /// <summary>同步口令；只修改设置且远端保险库未变时可以留空。</summary>
    [IpcOptional, IpcMaxLength(4096)] public string? Passphrase { get; init; }
}

public sealed record SyncSetupResultDto
{
    public required bool Success { get; init; }
    /// <summary>wrongPassphrase / passphraseRequired / weakPassphrase / vaultCorrupt 以及连接错误码</summary>
    [IpcOptional] public string? Error { get; init; }
    public required bool CreatedVault { get; init; }
}

public sealed record SyncDeviceDto
{
    public required string DeviceId { get; init; }
    [IpcOptional] public string? Name { get; init; }
    [IpcOptional] public string? AppVersion { get; init; }
    [IpcOptional] public string? Platform { get; init; }
    [IpcNumber] public required long LastSeq { get; init; }
    [IpcOptional, IpcNumber] public long? RemoteUpdatedAt { get; init; }
    public required bool IsCurrent { get; init; }
}

public sealed record TaskProgressDto
{
    [IpcStringValues("librarySync", "screenshotIndex", "publicData", "backup", "dataExport", "historyImport", "archiveCollect")]
    public required string Kind { get; init; }
    public required string Phase { get; init; }
    public required int Done { get; init; }
    /// <summary>0 表示总量未知，Done 是已处理数量。</summary>
    public required int Total { get; init; }
}

public sealed record SyncStatusDto
{
    public required bool Configured { get; init; }
    public required bool Enabled { get; init; }
    /// <summary>已配置但本机没有可用的密钥（例如 Linux 没有密钥环且应用重启过），需要重新输入口令。</summary>
    public required bool Locked { get; init; }
    public required bool CanPersistSecrets { get; init; }
    [IpcOptional] public SyncConfigDto? Config { get; init; }
    public required bool HasWebDavPassword { get; init; }
    public required bool HasS3Secret { get; init; }
    [IpcStringValues("idle", "running", "error")] public required string State { get; init; }
    /// <summary>pull / push / compact</summary>
    [IpcOptional] public string? Phase { get; init; }
    public required int Done { get; init; }
    public required int Total { get; init; }
    [IpcOptional, IpcNumber] public long? LastSyncAt { get; init; }
    [IpcOptional] public string? LastError { get; init; }
    public required int PendingChanges { get; init; }
    [IpcOptional] public string? VaultId { get; init; }
    [IpcOptional] public string? DeviceId { get; init; }
    public required IReadOnlyList<SyncDeviceDto> Devices { get; init; }
}

public sealed record SyncDisableRequest
{
    /// <summary>同时删除本机保存的服务器密码与同步密钥。</summary>
    public required bool ForgetCredentials { get; init; }
}

public sealed record SyncPassphraseChangeRequest
{
    [IpcMaxLength(4096)] public required string Passphrase { get; init; }
}

// ---------------------------------------------------------------------------
// 公共数据层：游戏类型 / 标签 / 开发商与成就 schema（DYH1319/steam-stat-public-data，经 GitHub Raw / jsDelivr）。
// ---------------------------------------------------------------------------

public sealed record PublicDataStatusDto
{
    public required bool Enabled { get; init; }
    [IpcStringValues("idle", "running")] public required string State { get; init; }
    /// <summary>清单的生成时间（Unix 秒）。</summary>
    [IpcOptional, IpcNumber] public long? GeneratedAt { get; init; }
    [IpcOptional, IpcNumber] public long? LastRefreshAt { get; init; }
    /// <summary>最近一次成功下载使用的镜像主机名。</summary>
    [IpcOptional] public string? Source { get; init; }
    /// <summary>清单中收录的游戏总数。</summary>
    public required int TotalApps { get; init; }
    public required int CachedApps { get; init; }
    public required int CachedAchievementApps { get; init; }
    public required int CachedShards { get; init; }
    [IpcNumber] public required long CacheBytes { get; init; }
    /// <summary>unreachable / invalid / io</summary>
    [IpcOptional] public string? LastError { get; init; }
}

public sealed record PublicDataEnabledRequest
{
    public required bool Enabled { get; init; }
}
