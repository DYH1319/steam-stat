namespace SteamStat.Contracts.Ipc;

// ---------------------------------------------------------------------------
// 平台、发布与社区：应用信息、更新通道、诊断包、Issue 反馈与崩溃报告
// ---------------------------------------------------------------------------

/// <summary>运行环境概要：平台、发行形态与更新方式。用于关于页、反馈页与更新卡片。</summary>
public sealed record AppInfoDto
{
    public required string Version { get; init; }
    [IpcStringValues("windows", "linux", "macos")] public required string Platform { get; init; }
    public required string Arch { get; init; }
    /// <summary>操作系统描述（如 "Arch Linux" / "Microsoft Windows 10.0.26100"）。</summary>
    public required string OsDescription { get; init; }
    /// <summary>installer | portable | appimage | archive | flatpak | aur | scoop | dev 等。</summary>
    public required string Distribution { get; init; }
    [IpcStringValues("auto", "notify", "managed")] public required string UpdateMode { get; init; }
    /// <summary>由包管理器更新时给出的命令（如 scoop update steam-stat）。</summary>
    [IpcOptional] public string? ManagedUpdateCommand { get; init; }
    /// <summary>便携模式：数据保存在程序目录下。</summary>
    public required bool PortableData { get; init; }
    public required string DataDirectory { get; init; }
    /// <summary>能否把 Steam 登录凭证安全地保存到磁盘（Linux 需要 Secret Service 密钥环）。</summary>
    public required bool SecureCredentialStorage { get; init; }
    /// <summary>Linux 图形会话类型（x11 / wayland）；Windows 为空。</summary>
    [IpcOptional] public string? SessionType { get; init; }
    public required string RepositoryUrl { get; init; }
    /// <summary>系统支持 Mica / 亚克力窗口材质（Windows 11 22H2 及以上）。</summary>
    public required bool WindowMaterialSupported { get; init; }
}

/// <summary>path 是渲染层路由（如 /game/730），Host 只接受白名单内的详情页。</summary>
public sealed record PopoutOpenRequest
{
    [IpcMaxLength(256)] public required string Path { get; init; }
}

public sealed record PopoutOpenResultDto
{
    /// <summary>false 表示路由不允许弹出、窗口数已到上限或创建失败。</summary>
    public required bool Opened { get; init; }
    /// <summary>同一页面已有窗口时只切到前台，不新建。</summary>
    public required bool Existing { get; init; }
    [IpcStringValues("invalid", "limit", "failed")] [IpcOptional] public string? Error { get; init; }
}

/// <summary>id 来自窗口地址的 popout 参数。Electron.NET 的 IPC 不带发送方，窗口按钮要靠它定位自己。</summary>
public sealed record PopoutControlRequest
{
    [IpcMaxLength(32)] public required string Id { get; init; }
    [IpcStringValues("state", "minimize", "maximize", "close")] public required string Action { get; init; }
}

public sealed record PopoutStateDto
{
    public required bool Maximized { get; init; }
}

public sealed record AppearanceBackgroundPickResultDto
{
    public required bool Cancelled { get; init; }
    /// <summary>复制到用户数据目录后的路径，原文件移动或删除不影响背景。</summary>
    [IpcOptional] public string? Path { get; init; }
    [IpcStringValues("tooLarge", "unsupported", "failed")] [IpcOptional] public string? Error { get; init; }
}

public sealed record DiagnosticsSettingsDto
{
    public required bool CrashReportsEnabled { get; init; }
}

public sealed record DiagnosticsSettingsPatchDto
{
    [IpcOptional] public bool? CrashReportsEnabled { get; init; }
}

public sealed record DiagnosticsBundleRequest
{
    /// <summary>包含最近几天的日志（1~14，默认 3）。</summary>
    [IpcOptional, IpcRange(1, 14)] public int? LogDays { get; init; }
}

public sealed record DiagnosticsBundleEntryDto
{
    public required string Name { get; init; }
    [IpcNumber] public required long SizeBytes { get; init; }
}

public sealed record DiagnosticsBundleDto
{
    public required string FileName { get; init; }
    /// <summary>诊断包完整路径（只用于界面展示，打开文件夹走 diagnosticsBundleReveal）。</summary>
    public required string FilePath { get; init; }
    [IpcNumber] public required long SizeBytes { get; init; }
    [IpcNumber] public required long CreatedAt { get; init; }
    public required IReadOnlyList<DiagnosticsBundleEntryDto> Entries { get; init; }
    /// <summary>本次脱敏替换的条目数（账号名、SteamID、路径等）。</summary>
    public required int RedactionCount { get; init; }
}

public sealed record DiagnosticsBundleRevealRequest
{
    [IpcMaxLength(128)] public required string FileName { get; init; }
}

/// <summary>打开预填好的 GitHub Issue。系统信息由 Host 生成并附在正文末尾。</summary>
public sealed record FeedbackIssueRequest
{
    [IpcStringValues("bug", "feature", "question")] public required string Kind { get; init; }
    [IpcOptional, IpcMaxLength(200)] public string? Title { get; init; }
    [IpcOptional, IpcMaxLength(4000)] public string? Description { get; init; }
    /// <summary>附上脱敏的系统信息（版本、系统、发行形态），默认附上。</summary>
    [IpcOptional] public bool? IncludeSystemInfo { get; init; }
    /// <summary>正文使用的语言（模板标题）。</summary>
    [IpcOptional, IpcStringValues("zh-CN", "en-US")] public string? Language { get; init; }
}

public sealed record CrashReportDto
{
    public required string Id { get; init; }
    public required string Fingerprint { get; init; }
    [IpcStringValues("host", "renderer")] public required string Source { get; init; }
    public required string Kind { get; init; }
    public required string ExceptionType { get; init; }
    public required string Message { get; init; }
    public required string StackTrace { get; init; }
    public required string AppVersion { get; init; }
    public required string Os { get; init; }
    public required string Distribution { get; init; }
    [IpcNumber] public required long FirstSeenAt { get; init; }
    [IpcNumber] public required long LastSeenAt { get; init; }
    public required int Count { get; init; }
    [IpcStringValues("pending", "submitted", "dismissed")] public required string Status { get; init; }
    [IpcOptional, IpcNumber] public long? SubmittedAt { get; init; }
    /// <summary>已自动上报到 Sentry 的时间；未配置自动上报或尚未成功时为空。</summary>
    [IpcOptional, IpcNumber] public long? UploadedAt { get; init; }
}

public sealed record CrashReportListDto
{
    public required bool Enabled { get; init; }
    /// <summary>手动提交方式：githubIssue（打开预填 Issue，需用户确认提交）。</summary>
    public required string Sink { get; init; }
    public required bool RequiresUserAction { get; init; }
    /// <summary>此构建配置了自动上报（Sentry）：开启崩溃报告后新记录会在后台自动发送。</summary>
    public required bool AutoUpload { get; init; }
    public required IReadOnlyList<CrashReportDto> Reports { get; init; }
}

public sealed record CrashReportIdRequest
{
    [IpcMaxLength(64)] public required string Id { get; init; }
}

public sealed record CrashReportDismissRequest
{
    /// <summary>为空时忽略全部待处理报告。</summary>
    [IpcOptional, IpcMaxLength(64)] public string? Id { get; init; }
}

public sealed record CrashReportSubmitResultDto
{
    public required bool Success { get; init; }
    [IpcOptional] public string? Url { get; init; }
}

/// <summary>渲染层未捕获的异常（Vue errorHandler / window.onerror / unhandledrejection）。</summary>
public sealed record RendererCrashReportRequest
{
    [IpcStringValues("rendererError", "unhandledRejection")] public required string Kind { get; init; }
    [IpcMaxLength(200)] public required string ExceptionType { get; init; }
    [IpcMaxLength(2000)] public required string Message { get; init; }
    [IpcMaxLength(16000)] public required string StackTrace { get; init; }
}
