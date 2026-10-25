namespace ElectronNet.Models;

/// <summary>
/// 一次 CSV 会话导入批次（可整体撤销）
/// </summary>
public class SessionImportBatch
{
    public int Id { get; init; }

    public string FileName { get; init; } = string.Empty;

    public int CreatedAt { get; init; }

    public int RowCount { get; set; }
}
