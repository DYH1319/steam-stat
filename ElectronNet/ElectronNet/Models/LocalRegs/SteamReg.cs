namespace ElectronNet.Models.LocalRegs;

public class SteamReg
{
    /// <summary>
    /// 
    /// </summary>
    public int AlreadyRetriedOfflineMode { get; set; }

    /// <summary>
    /// 自动登录的用户
    /// </summary>
    public string AutoLoginUser { get; set; } = string.Empty;

    /// <summary>
    /// 自动登录的用户（蒸汽平台）
    /// </summary>
    public string AutoLoginUserSteamChina { get; set; } = string.Empty;
    
    /// <summary>
    /// 
    /// </summary>
    public int CompletedOOBEStage1 { get; set; }

    /// <summary>
    /// 使用的语言
    /// </summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>
    /// 
    /// </summary>
    public string LastGameNameUsed { get; set; } = string.Empty;
    
    /// <summary>
    /// 
    /// </summary>
    public string PseudoUUID { get; set; } = string.Empty;
    
    /// <summary>
    /// 
    /// </summary>
    public string Rate { get; set; } = string.Empty;
    
    /// <summary>
    /// 
    /// </summary>
    public int RememberPassword { get; set; }
    
    /// <summary>
    /// 
    /// </summary>
    public int Restart { get; set; }
    
    /// <summary>
    /// 对好友显示的运行的单个 App 的 Id，没有运行任何 App 时会置 0
    /// </summary>
    public int RunningAppID { get; set; }
    
    /// <summary>
    /// 
    /// </summary>
    public string Skin { get; set; } = string.Empty;
    
    /// <summary>
    /// 
    /// </summary>
    public string SourceModInstallPath { get; set; } = string.Empty;
    
    /// <summary>
    /// 
    /// </summary>
    public int StartupModeTmp { get; set; }
    
    /// <summary>
    /// 
    /// </summary>
    public int StartupModeTmpIsValid { get; set; }
    
    /// <summary>
    /// Steam 可执行文件路径
    /// </summary>
    public string SteamExe { get; set; } = string.Empty;
    
    /// <summary>
    /// Steam 安装路径
    /// </summary>
    public string SteamPath { get; set; } = string.Empty;
    
    /// <summary>
    /// 
    /// </summary>
    public int SuppressAutoRun { get; set; }
}