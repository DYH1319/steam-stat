namespace ElectronNet.Models;

public class SessionImportBatch
{
    public string Id { get; init; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public int ImportedAt { get; set; }

    public int RowCount { get; set; }
}
