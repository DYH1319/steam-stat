namespace ElectronNet.Models.LocalFiles;

public class AppManifestAcf
{
    /// <summary>
    /// 应用 ID
    /// </summary>
    public int AppId { get; set; }

    /// <summary>
    /// 所属库路径
    /// </summary>
    public string LibraryPath { get; set; } = string.Empty;

    /// <summary>
    /// 
    /// </summary>
    public int Universe { get; set; }

    /// <summary>
    /// 启动器路径（一般为 Steam 可执行文件路径）
    /// </summary>
    public string LauncherPath { get; set; } = string.Empty;

    /// <summary>
    /// 应用名称（英文名称）
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 
    /// </summary>
    public int StateFlags { get; set; }

    /// <summary>
    /// 应用安装目录（相对于 {SteamLibraryPath\steamapps\common} 的路径）
    /// </summary>
    public string InstallDir { get; set; } = string.Empty;

    /// <summary>
    /// 应用最后更新 Unix 时间戳
    /// </summary>
    public int LastUpdated { get; set; }
    
    /// <summary>
    /// 应用最后游玩 Unix 时间戳
    /// </summary>
    public int LastPlayed { get; set; }
    
    /// <summary>
    /// 应用占用磁盘空间大小（不包含创意工坊文件占用空间）
    /// </summary>
    public long SizeOnDisk { get; set; }
    
    /// <summary>
    /// 
    /// </summary>
    public long StagingSize { get; set; }
    
    /// <summary>
    /// 
    /// </summary>
    public int BuildId { get; set; }
    
    /// <summary>
    /// 
    /// </summary>
    public long LastOwner { get; set; }
    
    /// <summary>
    /// 
    /// </summary>
    public int? DownloadType { get; set; }
    
    /// <summary>
    /// 
    /// </summary>
    public int? UpdateResult { get; set; }
    
    /// <summary>
    /// 
    /// </summary>
    public long? BytesToDownload { get; set; }
    
    /// <summary>
    /// 
    /// </summary>
    public long? BytesDownloaded { get; set; }
    
    /// <summary>
    /// 
    /// </summary>
    public long? BytesToStage { get; set; }
    
    /// <summary>
    /// 
    /// </summary>
    public long? BytesStaged { get; set; }
    
    /// <summary>
    /// 
    /// </summary>
    public int? TargetBuildID { get; set; }
    
    /// <summary>
    /// 
    /// </summary>
    public int AutoUpdateBehavior { get; set; }
    
    /// <summary>
    /// 是否允许运行此应用时进行下载
    /// </summary>
    public bool AllowOtherDownloadsWhileRunning { get; set; }
    
    /// <summary>
    /// 
    /// </summary>
    public int ScheduledAutoUpdate { get; set; }
    
    /// <summary>
    /// 
    /// </summary>
    public int? StagingFolder { get; set; }

    /// <summary>
    /// 
    /// </summary>
    public Dictionary<int, InstalledDepot> InstalledDepots { get; set; } = new();

    /// <summary>
    /// 
    /// </summary>
    public Dictionary<int, int>? SharedDepots { get; set; }
    
    /// <summary>
    /// 
    /// </summary>
    public Dictionary<int, string>? InstallScripts { get; set; }
    
    /// <summary>
    /// 
    /// </summary>
    public Config UserConfig { get; set; } = new();
    
    /// <summary>
    /// 
    /// </summary>
    public Config MountedConfig { get; set; } = new();
    
    public class InstalledDepot
    {
        /// <summary>
        /// 
        /// </summary>
        public ulong Manifest { get; set; }
        
        /// <summary>
        /// 
        /// </summary>
        public long Size { get; set; }
        
        /// <summary>
        /// 
        /// </summary>
        public int? DlcAppId { get; set; }
    }
    
    public class Config
    {
        /// <summary>
        /// 
        /// </summary>
        public string? Language { get; set; }
        
        /// <summary>
        /// 已禁用的 DLC，逗号分隔
        /// </summary>
        public string? DisabledDlc { get; set; }
        
        /// <summary>
        /// 
        /// </summary>
        public string? OptionalDlc { get; set; }
        
        /// <summary>
        /// 
        /// </summary>
        public string? BetaKey { get; set; }
    }
}