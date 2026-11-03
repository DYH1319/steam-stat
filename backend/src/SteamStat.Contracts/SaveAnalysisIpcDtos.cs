namespace SteamStat.Contracts.Ipc;

// ---------------------------------------------------------------------------
// 存档分析插件：Host 负责插件管理、按清单定位并只读读取原始存档；
// 解析在渲染层沙箱中由插件完成，产出声明式报告。
// ---------------------------------------------------------------------------

public sealed record LocalizedTextDto
{
    public required string ZhCn { get; init; }
    public required string EnUs { get; init; }
}

public sealed record SaveAnalysisPluginDto
{
    public required string Id { get; init; }
    public required string Version { get; init; }
    public required int ApiVersion { get; init; }
    public required LocalizedTextDto Name { get; init; }
    public required LocalizedTextDto Description { get; init; }
    [IpcOptional] public string? Author { get; init; }
    [IpcOptional] public string? Homepage { get; init; }
    public required IReadOnlyList<int> AppIds { get; init; }
    public required string Entry { get; init; }
    public required IReadOnlyList<string> Data { get; init; }
    /// <summary>清单 steamAchievements：需要 Steam 官方成就文本。</summary>
    public required bool UsesSteamAchievements { get; init; }
    public required IReadOnlyList<string> ImageHosts { get; init; }
    [IpcNumber] public required long MaxFileSize { get; init; }
    /// <summary>user = 用户插件目录（含从仓库安装的插件）；dev = 开发目录（STEAM_STAT_PLUGINS_DIR，只读）。</summary>
    [IpcStringValues("user", "dev")] public required string Source { get; init; }
    public required bool Enabled { get; init; }
    /// <summary>当前运行时是否支持该插件的 apiVersion。</summary>
    public required bool Compatible { get; init; }
    public required string Directory { get; init; }
}

public sealed record SaveAnalysisPluginErrorDto
{
    public required string Directory { get; init; }
    public required string Error { get; init; }
}

public sealed record SaveAnalysisPluginListDto
{
    public required IReadOnlyList<SaveAnalysisPluginDto> Plugins { get; init; }
    /// <summary>无法加载的插件目录（清单缺失或不合法）。</summary>
    public required IReadOnlyList<SaveAnalysisPluginErrorDto> Errors { get; init; }
    public required string UserDirectory { get; init; }
    [IpcOptional] public string? DevDirectory { get; init; }
}

public sealed record SaveAnalysisPluginIdRequest
{
    [IpcMaxLength(64)] public required string PluginId { get; init; }
}

public sealed record SaveAnalysisPluginEnabledRequest
{
    [IpcMaxLength(64)] public required string PluginId { get; init; }
    public required bool Enabled { get; init; }
}

public sealed record SaveAnalysisFileDto
{
    /// <summary>读取凭据：渲染层只能用它读取 Host 定位或用户选择过的文件。</summary>
    public required string Token { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
    [IpcOptional] public string? LocationId { get; init; }
    [IpcOptional] public LocalizedTextDto? LocationLabel { get; init; }
    [IpcOptional] public string? SteamId { get; init; }
    [IpcNumber] public required long Size { get; init; }
    [IpcNumber] public required long ModifiedAt { get; init; }
}

public sealed record SaveAnalysisPickResultDto
{
    public required SaveAnalysisFileDto File { get; init; }
    /// <summary>按清单 detect 规则（文件名 + 文件头）匹配到的插件。</summary>
    public required IReadOnlyList<string> PluginIds { get; init; }
}

public sealed record SaveAnalysisReadRequest
{
    [IpcMaxLength(64)] public required string Token { get; init; }
    [IpcNumber, IpcRange(0, 1_073_741_824)] public required long Offset { get; init; }
    [IpcRange(1, 524_288)] public required int Length { get; init; }
}

public sealed record SaveAnalysisChunkDto
{
    public required bool Success { get; init; }
    [IpcOptional, IpcStringValues("notFound", "tooLarge", "unreadable", "expired")] public string? Error { get; init; }
    public required string Base64 { get; init; }
    [IpcNumber] public required long Offset { get; init; }
    [IpcNumber] public required long TotalSize { get; init; }
}

public sealed record SaveAnalysisPluginFileRequest
{
    [IpcMaxLength(64)] public required string PluginId { get; init; }
    [IpcMaxLength(256)] public required string Path { get; init; }
}

public sealed record SaveAnalysisPluginFileDto
{
    public required bool Success { get; init; }
    [IpcOptional, IpcStringValues("notFound", "notDeclared", "tooLarge")] public string? Error { get; init; }
    public required string Content { get; init; }
}

public sealed record SaveAnalysisRepositoryPluginDto
{
    public required string Id { get; init; }
    public required string Version { get; init; }
    public required int ApiVersion { get; init; }
    public required LocalizedTextDto Name { get; init; }
    public required LocalizedTextDto Description { get; init; }
    [IpcOptional] public string? Author { get; init; }
    public required IReadOnlyList<int> AppIds { get; init; }
    [IpcNumber] public required long Size { get; init; }
    [IpcOptional] public string? InstalledVersion { get; init; }
    public required bool UpdateAvailable { get; init; }
    public required bool Compatible { get; init; }
}

public sealed record SaveAnalysisRepositoryDto
{
    public required bool Success { get; init; }
    [IpcOptional, IpcStringValues("unreachable", "invalidIndex")] public string? Error { get; init; }
    [IpcOptional] public string? Source { get; init; }
    public required IReadOnlyList<SaveAnalysisRepositoryPluginDto> Plugins { get; init; }
}

public sealed record SaveAnalysisInstallResultDto
{
    public required bool Success { get; init; }
    [IpcOptional, IpcStringValues("notFound", "unreachable", "integrity", "invalidManifest", "incompatible", "failed")]
    public string? Error { get; init; }
    [IpcOptional] public string? Version { get; init; }
}
