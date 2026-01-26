namespace ElectronNet.Models.LocalRegs;

public class SteamActiveProcessReg
{
    /// <summary>
    /// 当前登录用户的 AccountID，Steam 正常退出 / 切换用户时会置 0
    /// </summary>
    public int ActiveUser { get; set; }

    /// <summary>
    /// Steam 进程 ID，Steam 正常退出时会置 0
    /// </summary>
    public int Pid { get; set; }

    /// <summary>
    /// steamclient.dll 文件路径
    /// </summary>
    public string SteamClientDll { get; set; } = string.Empty;

    /// <summary>
    /// steamclient64.dll 文件路径
    /// </summary>
    public string SteamClientDll64 { get; set; } = string.Empty;

    /// <summary>
    /// 
    /// </summary>
    public string Universe { get; set; } = string.Empty;
}