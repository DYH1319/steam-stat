namespace ElectronNet.Models.LocalFiles;

public class LoginUsersVdf
{
    /// <summary>
    /// Steam ID
    /// </summary>
    public string SteamID { get; set; } = string.Empty;

    /// <summary>
    /// 账号名
    /// </summary>
    public string AccountName { get; set; } = string.Empty;

    /// <summary>
    /// 昵称
    /// </summary>
    public string PersonaName { get; set; } = string.Empty;

    /// <summary>
    /// 是否记住密码
    /// </summary>
    public bool RememberPassword { get; set; }

    /// <summary>
    /// 是否离线模式
    /// </summary>
    public bool WantsOfflineMode { get; set; }

    /// <summary>
    /// 跳过离线模式警告（暂无实际性作用）
    /// </summary>
    public bool SkipOfflineModeWarning { get; set; }

    /// <summary>
    /// 是否自动登录
    /// </summary>
    public bool AllowAutoLogin { get; set; }

    /// <summary>
    /// 是否最近登录
    /// </summary>
    public bool MostRecent { get; set; }

    /// <summary>
    /// 最近登录时间
    /// </summary>
    public int Timestamp { get; set; }
}