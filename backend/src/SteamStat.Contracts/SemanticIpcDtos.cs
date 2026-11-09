namespace SteamStat.Contracts.Ipc;

// ---------------------------------------------------------------------------
// 语义视图层、指标字典、可解释的数字、档案体检、数据库快照、年度封存包
// ---------------------------------------------------------------------------

public sealed record BilingualTextDto(string Zh, string En);

public sealed record SemanticColumnDto
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required BilingualTextDto Description { get; init; }
}

public sealed record SemanticViewDto
{
    public required string Name { get; init; }
    [IpcStringValues("aggregate", "games", "notes", "friends")] public required string Tier { get; init; }
    public required BilingualTextDto Description { get; init; }
    public required IReadOnlyList<SemanticColumnDto> Columns { get; init; }
}

public sealed record MetricDefinitionDto
{
    public required string Id { get; init; }
    public required BilingualTextDto Name { get; init; }
    public required BilingualTextDto Definition { get; init; }
    public required BilingualTextDto Formula { get; init; }
    public BilingualTextDto? Caveat { get; init; }
    [IpcStringValues("seconds", "count", "percent")] public required string Unit { get; init; }
    [IpcStringValues("recorded", "official", "derived")] public required string Evidence { get; init; }
    public required IReadOnlyList<string> Views { get; init; }
    public required string Sql { get; init; }
    /// <summary>当前值。隐藏游戏已剔除。查询失败时为空。</summary>
    public double? Value { get; init; }
}

public sealed record SemanticCatalogDto
{
    public required int ContractVersion { get; init; }
    public required IReadOnlyList<SemanticViewDto> Views { get; init; }
    public required IReadOnlyList<MetricDefinitionDto> Metrics { get; init; }
}

public sealed record SemanticQueryRequest
{
    public required string Sql { get; init; }
}

public sealed record SemanticQueryResultDto
{
    [IpcStringValues("ok", "denied", "invalid", "timeout")] public required string Status { get; init; }
    public required IReadOnlyList<string> Columns { get; init; }
    /// <summary>行数据的 JSON 数组（二维）。列类型不固定，保持原样交给调用方。</summary>
    public required string RowsJson { get; init; }
    public required int RowCount { get; init; }
    public required bool Truncated { get; init; }
    [IpcNumber] public required long ElapsedMilliseconds { get; init; }
}

public sealed record StatsExplainRequest
{
    [IpcOptional] public IReadOnlyList<string>? SteamIds { get; init; }
    [IpcOptional] public IReadOnlyList<int>? AppIds { get; init; }
    /// <summary>逻辑日（YYYY-MM-DD），含两端。不传时不限。</summary>
    [IpcOptional] public string? FromDay { get; init; }
    [IpcOptional] public string? ToDay { get; init; }
    /// <summary>
    /// 范围的口径，要和被解释的数字一致。false（默认）：按开始时间归属，整局计入，统计页汇总用这个。
    /// true：按逻辑日截断，只算落在范围内的部分，仪表盘的今日 / 本周 / 本月用这个。
    /// </summary>
    [IpcOptional] public bool? ClipToRange { get; init; }
    [IpcOptional, IpcRange(1, 500)] public int? Limit { get; init; }
}

public sealed record ExplainDeductionDto
{
    [IpcStringValues("excluded", "hidden", "belowMinimum", "idle", "open")] public required string Kind { get; init; }
    public required int Records { get; init; }
    [IpcNumber] public required long Seconds { get; init; }
}

public sealed record ExplainSourceDto
{
    [IpcStringValues("recorded", "recovered", "imported", "edited")] public required string Evidence { get; init; }
    public required int Records { get; init; }
    [IpcNumber] public required long Seconds { get; init; }
}

public sealed record ExplainSessionDto
{
    public required int RecordId { get; init; }
    public required int AppId { get; init; }
    public string? AppName { get; init; }
    public required string SteamId { get; init; }
    public required int StartTime { get; init; }
    public required int EndTime { get; init; }
    [IpcNumber] public required long Seconds { get; init; }
    [IpcNumber] public required long IdleSeconds { get; init; }
    [IpcStringValues("recorded", "recovered", "imported", "edited")] public required string Evidence { get; init; }
    public string? DeviceName { get; init; }
    public string? EndReason { get; init; }
}

public sealed record StatsExplainDto
{
    /// <summary>与界面同口径的结果，范围的算法见 <see cref="StatsExplainRequest.ClipToRange"/>。</summary>
    [IpcNumber] public required long TotalSeconds { get; init; }
    [IpcNumber] public required long EffectiveSeconds { get; init; }
    public required int SessionCount { get; init; }
    [IpcNumber] public required long RawSeconds { get; init; }
    public required int RawRecords { get; init; }
    public required int MergedRecords { get; init; }
    public required IReadOnlyList<ExplainDeductionDto> Deductions { get; init; }
    public required IReadOnlyList<ExplainSourceDto> Sources { get; init; }
    public required IReadOnlyList<ExplainSessionDto> Sessions { get; init; }
    public required bool SessionsTruncated { get; init; }
    public required int DayBoundaryHour { get; init; }
    public required int MinDurationSeconds { get; init; }
    public required int MergeGapSeconds { get; init; }
}

public sealed record ArchiveHealthCheckDto
{
    public required string Id { get; init; }
    [IpcStringValues("ok", "warning", "error", "unknown")] public required string Status { get; init; }
    /// <summary>界面用它和 <see cref="Id"/> 取文案；数字放在 Values 里。</summary>
    public required IReadOnlyDictionary<string, string> Values { get; init; }
    /// <summary>修复建议的跳转目标（页面路由），没有时为空。</summary>
    public string? ActionRoute { get; init; }
}

public sealed record ArchiveHealthGapDto
{
    public required string FromDay { get; init; }
    public required string ToDay { get; init; }
    public required int Days { get; init; }
    [IpcNumber] public required long UntrackedSeconds { get; init; }
}

public sealed record ArchiveHealthReportDto
{
    [IpcNumber] public required long CheckedAt { get; init; }
    public required IReadOnlyList<ArchiveHealthCheckDto> Checks { get; init; }
    public required IReadOnlyList<ArchiveHealthGapDto> Gaps { get; init; }
}

public sealed record DatabaseSnapshotDto
{
    public required string Name { get; init; }
    [IpcNumber] public required long CreatedAt { get; init; }
    [IpcNumber] public required long SizeBytes { get; init; }
    [IpcStringValues("startup", "preRestore", "preMigration")] public required string Kind { get; init; }
}

public sealed record DatabaseSnapshotRestoreRequest
{
    public required string Name { get; init; }
}

public sealed record ArchiveSealDto
{
    public required int Year { get; init; }
    [IpcNumber] public required long SealedAt { get; init; }
    [IpcNumber] public required long SizeBytes { get; init; }
    public required string Sha256 { get; init; }
    public required string AppVersion { get; init; }
    public required int SessionCount { get; init; }
    /// <summary>文件还在且内容和封存时一致。</summary>
    public required bool Intact { get; init; }
}

public sealed record ArchiveSealOverviewDto
{
    public required IReadOnlyList<ArchiveSealDto> Seals { get; init; }
    /// <summary>有数据但还没有封存的已结束年份。</summary>
    public required IReadOnlyList<int> AvailableYears { get; init; }
}

public sealed record ArchiveSealCreateRequest
{
    [IpcRange(2003, 2200)] public required int Year { get; init; }
}

public sealed record ArchiveSealResultDto
{
    [IpcStringValues("sealed", "exists", "notFinished", "empty", "failed")] public required string Status { get; init; }
    public ArchiveSealDto? Seal { get; init; }
}

public sealed record ArchiveSealRevealRequest
{
    [IpcRange(2003, 2200)] public required int Year { get; init; }
}

public sealed record RichPresenceCoverageDto
{
    public required string AccountName { get; init; }
    [IpcNumber] public required long Since { get; init; }
    /// <summary>经推送收到的更新数。</summary>
    [IpcNumber] public required long PushUpdates { get; init; }
    /// <summary>对账轮询的回复数。</summary>
    [IpcNumber] public required long PollResponses { get; init; }
    /// <summary>落库的状态变化数。</summary>
    [IpcNumber] public required long RecordedChanges { get; init; }
    /// <summary>其中只有轮询发现、推送漏掉的变化数。这个数字高说明推送通道不可靠。</summary>
    [IpcNumber] public required long ChangesCaughtByPoll { get; init; }
    /// <summary>当前有状态的人数。</summary>
    public required int TrackedPresences { get; init; }
    [IpcNumber] public long? LastPushAt { get; init; }
    [IpcNumber] public long? LastPollAt { get; init; }
}
