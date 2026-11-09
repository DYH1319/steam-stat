namespace SteamStat.Contracts.Ipc;

public sealed record InsightReportRequest
{
    [IpcOptional] public IReadOnlyList<string>? SteamIds { get; init; }
    /// <summary>洞察看最近多少天，默认 30。这里不传日期字符串：桥接层会把它转成 DateTime。</summary>
    [IpcOptional, IpcRange(7, 3650)] public int? PeriodDays { get; init; }
}

public sealed record InsightItemDto
{
    public required string Id { get; init; }
    [IpcStringValues("behavior", "causal", "forecast", "anomaly")] public required string Kind { get; init; }
    public required string FromDay { get; init; }
    public required string ToDay { get; init; }
    public required BilingualTextDto Text { get; init; }
    public required int SampleSize { get; init; }
    public required double Confidence { get; init; }
    /// <summary>recorded / official / derived / unknown。</summary>
    public required string Evidence { get; init; }
    public required IReadOnlyList<string> Views { get; init; }
    [IpcOptional] public int? AppId { get; init; }
}

public sealed record PersonaAxisDto
{
    [IpcStringValues("explore", "social", "night", "burst")] public required string Id { get; init; }
    /// <summary>-1~1，正值是前一个字母。</summary>
    public required double Score { get; init; }
    public required string Letter { get; init; }
    public required double Value { get; init; }
}

public sealed record PersonaYearDto
{
    public required int Year { get; init; }
    public required string Code { get; init; }
    public required BilingualTextDto Name { get; init; }
    public required BilingualTextDto Summary { get; init; }
    public required IReadOnlyList<PersonaAxisDto> Axes { get; init; }
    public required BilingualTextDto Title { get; init; }
    public required IReadOnlyList<BilingualTextDto> TitleReasons { get; init; }
    public required int Sessions { get; init; }
    public required double Hours { get; init; }
}

public sealed record CareerHighlightDto
{
    public required string Kind { get; init; }
    public required int Time { get; init; }
    public required string Day { get; init; }
    [IpcOptional] public int? AppId { get; init; }
    public required BilingualTextDto Text { get; init; }
}

public sealed record SpectacleItemDto
{
    public required int AppId { get; init; }
    public required string Name { get; init; }
    public required double Value { get; init; }
    [IpcOptional] public string? Currency { get; init; }
    [IpcOptional] public int? Time { get; init; }
    public required int PlaytimeMinutes { get; init; }
}

public sealed record SpectacleBoardDto
{
    public required IReadOnlyList<SpectacleItemDto> PriciestUnplayed { get; init; }
    public required IReadOnlyList<SpectacleItemDto> LongestUntouched { get; init; }
    public required IReadOnlyList<SpectacleItemDto> Delisted { get; init; }
}

public sealed record ComebackCauseDto
{
    [IpcStringValues("update", "dlc", "friend", "holiday", "sale")] public required string Kind { get; init; }
    [IpcOptional] public int? DaysBefore { get; init; }
    [IpcOptional] public BilingualTextDto? Name { get; init; }
}

public sealed record ComebackDto
{
    public required int AppId { get; init; }
    public required string Name { get; init; }
    public required int Start { get; init; }
    public required string Day { get; init; }
    public required int GapDays { get; init; }
    public required int Seconds { get; init; }
    public required IReadOnlyList<ComebackCauseDto> Causes { get; init; }
}

public sealed record CrashBurstDto
{
    public required int AppId { get; init; }
    public required string Name { get; init; }
    public required int Start { get; init; }
    public required int End { get; init; }
    public required int Attempts { get; init; }
    public required int Seconds { get; init; }
    public required bool AfterUpdate { get; init; }
    [IpcOptional] public int? BuildId { get; init; }
}

/// <summary>洞察页需要的全部内容。读一次档案算完，一次发给渲染层。</summary>
public sealed record InsightReportDto
{
    public required int GeneratedAt { get; init; }
    public required string FromDay { get; init; }
    public required string ToDay { get; init; }
    public required IReadOnlyList<InsightItemDto> Insights { get; init; }
    public required IReadOnlyList<PersonaYearDto> Personas { get; init; }
    public required IReadOnlyList<CareerHighlightDto> Career { get; init; }
    public required SpectacleBoardDto Spectacle { get; init; }
    public required IReadOnlyList<ComebackDto> Comebacks { get; init; }
    public required IReadOnlyList<CrashBurstDto> Crashes { get; init; }
}

public sealed record WeeklyIssueSummaryDto
{
    public required int Id { get; init; }
    [IpcStringValues("weekly", "extra")] public required string Kind { get; init; }
    public required int Number { get; init; }
    public required string FromDay { get; init; }
    public required string ToDay { get; init; }
    public required int GeneratedAt { get; init; }
    [IpcOptional] public int? ReadAt { get; init; }
    public required bool Backfilled { get; init; }
    public required BilingualTextDto Headline { get; init; }
    [IpcOptional] public int? AppId { get; init; }
}

public sealed record WeeklyIssueDto
{
    public required WeeklyIssueSummaryDto Summary { get; init; }
    /// <summary>冻结的内容（JSON）。旧期号的结构可能和新版本不同，渲染层自己解析并容错。</summary>
    public required string Payload { get; init; }
}

public sealed record WeeklyIssuesQueryRequest
{
    [IpcRange(1, 200)] public required int Limit { get; init; }
    [IpcOptional, IpcRange(1, int.MaxValue)] public int? BeforeId { get; init; }
}

public sealed record WeeklyIssueIdRequest
{
    [IpcRange(1, int.MaxValue)] public required int Id { get; init; }
}

public sealed record WeeklyBackfillRequest
{
    /// <summary>这一周里的任意时刻（Unix 秒）。这里不传日期字符串：桥接层会把它转成 DateTime。</summary>
    [IpcRange(1, int.MaxValue)] public required int AnchorTime { get; init; }
}

public sealed record WeeklyBackfillResultDto
{
    [IpcStringValues("created", "exists", "empty", "notFinished", "disabled")] public required string Status { get; init; }
    [IpcOptional] public WeeklyIssueSummaryDto? Issue { get; init; }
}
