namespace ElectronNet.Models.LocalRegs;

public class SteamAppReg
{
    /// <summary>
    /// 应用 ID
    /// </summary>
    public int AppId { get; set; }
    
    /// <summary>
    /// 
    /// </summary>
    public int? Firewall { get; set; }
    
    /// <summary>
    /// 
    /// </summary>
    public int? FmSysInfo { get; set; }
    
    /// <summary>
    /// 
    /// </summary>
    public int? Cloud { get; set; }
    
    /// <summary>
    /// 是否已安装
    /// </summary>
    public int? Installed { get; set; }
    
    /// <summary>
    /// 应用名称（英文名称）
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// 是否正在运行
    /// </summary>
    public int? Running { get; set; }

    /// <summary>
    /// 是否正在更新
    /// </summary>
    public int? Updating { get; set; }
}