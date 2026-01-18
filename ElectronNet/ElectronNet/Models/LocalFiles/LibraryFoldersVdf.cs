namespace ElectronNet.Models.LocalFiles;

public class LibraryFoldersVdf
{
    /// <summary>
    /// 库文件夹索引
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// 库文件夹路径
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// 库标签（设置后优先显示库标签；未设置时在 Windows 下默认显示磁盘名称）
    /// </summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// 
    /// </summary>
    public long ContentId { get; set; }

    /// <summary>
    /// 库总占用磁盘空间大小
    /// </summary>
    public long TotalSize { get; set; }

    /// <summary>
    /// 
    /// </summary>
    public long UpdateCleanBytesTally { get; set; }

    /// <summary>
    /// 
    /// </summary>
    public int TimeLastUpdateVerified { get; set; }

    /// <summary>
    /// 库中应用 ID -> 应用占用空间大小（不包括创意工坊）
    /// </summary>
    public Dictionary<int, long> Apps { get; set; } = new();
}