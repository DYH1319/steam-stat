namespace SteamStat.Contracts.Ipc;

// ---------------------------------------------------------------------------
// AI（自带 Key）、本地只读接口（MCP / Prometheus / OBS 叠加层 / 命令行）、审计日志、隐藏终端
//
// 用户写的文字（问题、对话历史、Key、终端命令）编码成「s1:」+ Base64 再传：
// 桥接层会把形如日期、GUID 的字符串改写格式。请求标识用非十六进制前缀，原因相同。
// ---------------------------------------------------------------------------

public sealed record AssistantStatusDto
{
    public required bool Enabled { get; init; }
    [IpcStringValues("anthropic", "openai", "ollama")] public required string Provider { get; init; }
    public required string Model { get; init; }
    /// <summary>实际使用的接口地址（设置为空时是服务商的默认地址）。</summary>
    public required string EffectiveBaseUrl { get; init; }
    public required bool HasKey { get; init; }
    public required bool KeyRequired { get; init; }
    /// <summary>Key 能否安全保存到磁盘。Linux 没有密钥环时只在本次运行有效。</summary>
    public required bool CanPersistKey { get; init; }
    /// <summary>设置里授权的级别。</summary>
    public required IReadOnlyList<string> Scopes { get; init; }
    /// <summary>实际生效的级别：主播模式下去掉好友。</summary>
    public required IReadOnlyList<string> EffectiveScopes { get; init; }
    /// <summary>开着、有 Key（或不需要）、填了模型，可以发请求了。</summary>
    public required bool Ready { get; init; }
}

public sealed record AssistantKeySetRequest
{
    /// <summary>s1: 编码。空字符串表示删除。</summary>
    [IpcMaxLength(2000)] public required string Key { get; init; }
}

public sealed record AssistantModelsDto
{
    [IpcStringValues("ok", "disabled", "noKey", "unauthorized", "unreachable", "failed")] public required string Status { get; init; }
    public required IReadOnlyList<string> Models { get; init; }
}

public sealed record AssistantTurnDto
{
    [IpcStringValues("user", "assistant")] public required string Role { get; init; }
    /// <summary>s1: 编码，解码后最多 8000 字。</summary>
    [IpcMaxLength(43000)] public required string Text { get; init; }
}

public sealed record AssistantAskRequest
{
    /// <summary>渲染层生成，形如 q-12，用来对应进度事件和取消。</summary>
    [IpcMaxLength(40)] public required string RequestId { get; init; }
    /// <summary>s1: 编码，解码后最多 2000 字。</summary>
    [IpcMaxLength(11000)] public required string Question { get; init; }
    [IpcOptional] public IReadOnlyList<AssistantTurnDto>? History { get; init; }
    [IpcStringValues("zh-CN", "en-US")] public required string Language { get; init; }
}

public sealed record AssistantCancelRequest
{
    [IpcMaxLength(40)] public required string RequestId { get; init; }
}

/// <summary>图表提示：渲染层按它把证据画成折线或柱状图。</summary>
public sealed record AssistantChartDto
{
    [IpcStringValues("line", "bar")] public required string Kind { get; init; }
    public required string X { get; init; }
    public required IReadOnlyList<string> Y { get; init; }
}

/// <summary>一次工具调用：模型看到的就是这些行，回答里的数字都应该能在这里找到。</summary>
public sealed record AssistantEvidenceDto
{
    public required string Tool { get; init; }
    public required string Arguments { get; init; }
    public required IReadOnlyList<string> Columns { get; init; }
    /// <summary>行（JSON 数组的数组），已经过打码。</summary>
    public required string RowsJson { get; init; }
    public required int RowCount { get; init; }
    public required bool Truncated { get; init; }
    [IpcOptional] public string? Error { get; init; }
    [IpcOptional] public AssistantChartDto? Chart { get; init; }
}

public sealed record AssistantAnswerDto
{
    [IpcStringValues("ok", "disabled", "noKey", "noModel", "unauthorized", "unreachable", "failed", "cancelled", "limit")]
    public required string Status { get; init; }
    public required string Text { get; init; }
    public required IReadOnlyList<AssistantEvidenceDto> Evidence { get; init; }
    public required string Model { get; init; }
    /// <summary>这次回答发给模型的请求数（每一步一条审计记录）。</summary>
    public required int Requests { get; init; }
}

public sealed record AssistantProgressDto
{
    public required string RequestId { get; init; }
    [IpcStringValues("thinking", "tool")] public required string Phase { get; init; }
    [IpcOptional] public string? Tool { get; init; }
    public required int Step { get; init; }
}

public sealed record AssistantWriteResultDto
{
    [IpcStringValues("ok", "disabled", "noKey", "noModel", "unauthorized", "unreachable", "failed", "scope", "empty", "exists")]
    public required string Status { get; init; }
    [IpcOptional] public string? Text { get; init; }
    /// <summary>不满足时缺的授权级别（Status = scope）。</summary>
    [IpcOptional] public string? MissingScope { get; init; }
}

public sealed record AssistantRecapGameDto
{
    public required int AppId { get; init; }
    /// <summary>s1: 编码。</summary>
    [IpcMaxLength(700)] public required string Name { get; init; }
    [IpcNumber] public required long Seconds { get; init; }
}

/// <summary>报告封面的开场白：数字是渲染层报告里已经算好的，Host 只负责打码和写作。</summary>
public sealed record AssistantRecapRequest
{
    [IpcStringValues("zh-CN", "en-US")] public required string Language { get; init; }
    [IpcStringValues("week", "month", "year")] public required string Unit { get; init; }
    /// <summary>s1: 编码的周期名，比如「2026 年 11 月」。</summary>
    [IpcMaxLength(400)] public required string Period { get; init; }
    [IpcNumber] public required long Seconds { get; init; }
    [IpcNumber] public required long PreviousSeconds { get; init; }
    public required int Sessions { get; init; }
    public required int Games { get; init; }
    public required int ActiveDays { get; init; }
    public required int NewGames { get; init; }
    [IpcOptional, IpcNumber] public long? LongestSessionSeconds { get; init; }
    [IpcOptional] public int? LongestSessionAppId { get; init; }
    [IpcOptional] public int? StreakDays { get; init; }
    /// <summary>最晚一局的结束时刻（当天 0 点起的分钟数，可超过 1440）。</summary>
    [IpcOptional] public int? LatestNightMinutes { get; init; }
    [IpcOptional] public IReadOnlyList<AssistantRecapGameDto>? TopGames { get; init; }
}

public sealed record AssistantChapterNamesRequest
{
    [IpcOptional] public IReadOnlyList<string>? SteamIds { get; init; }
    [IpcStringValues("zh-CN", "en-US")] public required string Language { get; init; }
}

public sealed record AssistantChapterNameDto
{
    public required string FromMonth { get; init; }
    public required string ToMonth { get; init; }
    public required string Title { get; init; }
}

public sealed record AssistantChapterNamesDto
{
    [IpcStringValues("ok", "disabled", "noKey", "noModel", "unauthorized", "unreachable", "failed", "scope", "empty", "exists")]
    public required string Status { get; init; }
    public required IReadOnlyList<AssistantChapterNameDto> Names { get; init; }
}

public sealed record AssistantPreviouslyOnRequest
{
    [IpcRange(1, int.MaxValue)] public required int AppId { get; init; }
    [IpcOptional] public IReadOnlyList<string>? SteamIds { get; init; }
    [IpcStringValues("zh-CN", "en-US")] public required string Language { get; init; }
}

// ---------------------------------------------------------------------------
// 审计日志：每次发给模型的完整请求、每次本地接口返回给调用方的内容
// ---------------------------------------------------------------------------

public sealed record AuditLogQueryRequest
{
    [IpcRange(1, 200)] public required int Limit { get; init; }
    [IpcOptional, IpcNumber] public long? BeforeId { get; init; }
    [IpcOptional, IpcStringValues("ai", "api", "mcp")] public string? Channel { get; init; }
}

public sealed record AuditEntrySummaryDto
{
    [IpcNumber] public required long Id { get; init; }
    [IpcNumber] public required long At { get; init; }
    [IpcStringValues("ai", "api", "mcp")] public required string Channel { get; init; }
    /// <summary>ask、weeklyEditorial、chapterNames、recap、previouslyOn、models、test；本地接口是工具名、query、cli。</summary>
    public required string Kind { get; init; }
    /// <summary>发往哪里：模型服务的主机名和模型，或本地调用方。</summary>
    public required string Destination { get; init; }
    public required string Summary { get; init; }
    public required IReadOnlyList<string> Scopes { get; init; }
    public required int Bytes { get; init; }
    public required bool Truncated { get; init; }
}

public sealed record AuditEntryIdRequest
{
    [IpcNumber] public required long Id { get; init; }
}

public sealed record AuditEntryDto
{
    public required AuditEntrySummaryDto Summary { get; init; }
    /// <summary>当时发出去的原文（JSON）。超过上限时截断并标记。</summary>
    public required string Content { get; init; }
}

// ---------------------------------------------------------------------------
// 本地只读接口
// ---------------------------------------------------------------------------

public sealed record LocalApiStatusDto
{
    public required bool Enabled { get; init; }
    public required bool Running { get; init; }
    public required int Port { get; init; }
    /// <summary>portInUse：端口被占用；failed：其他原因启动失败。</summary>
    [IpcOptional, IpcStringValues("portInUse", "failed")] public string? Error { get; init; }
    public required string BaseUrl { get; init; }
    public required string Token { get; init; }
    public required string OverlayUrl { get; init; }
    public required string McpUrl { get; init; }
    public required string MetricsUrl { get; init; }
    /// <summary>命令行工具的位置；开发模式下没有打包时为空。</summary>
    [IpcOptional] public string? CliPath { get; init; }
    public required IReadOnlyList<string> EffectiveScopes { get; init; }
}

// ---------------------------------------------------------------------------
// 隐藏终端：和命令行工具共用 Host 里的同一套命令
// ---------------------------------------------------------------------------

public sealed record TerminalRunRequest
{
    /// <summary>s1: 编码，解码后最多 2000 字。</summary>
    [IpcMaxLength(11000)] public required string Line { get; init; }
    [IpcStringValues("zh-CN", "en-US")] public required string Language { get; init; }
}

public sealed record TerminalOutputDto
{
    public required string Output { get; init; }
    public required int ExitCode { get; init; }
    public required bool Clear { get; init; }
}
