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
