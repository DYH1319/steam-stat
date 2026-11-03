namespace SteamStat.Core.Platform;

public interface ISecretStore
{
    /// <summary>Returns the protected form of <paramref name="plainText"/>, or null when protection fails. Never returns unprotected plaintext for a non-empty input.</summary>
    string? Protect(string? plainText);
    string? Unprotect(string? protectedText);

    /// <summary>Whether <paramref name="value"/> is protected in a form that is safe to persist.</summary>
    bool IsProtected(string? value);

    /// <summary>
    /// 能否把凭证安全地保存到磁盘（Windows DPAPI 始终可以；Linux 需要可用的 Secret Service 密钥环）。
    /// 为 false 时登录仍可用，但「记住登录」不会写入数据库。
    /// </summary>
    bool CanPersist => true;
}

public interface ISteamInstallLocator
{
    SteamRegistrySnapshot ReadSteamRegistry();
    SteamActiveProcessSnapshot ReadActiveProcess();
    IReadOnlyDictionary<int, SteamAppRegistrySnapshot> ReadAppRegistry();
    bool HasRegistryKey(SteamRegistryKey key);
    void SetAutoLoginUser(string accountName, bool rememberPassword);

    /// <summary>
    /// 启动 Steam 客户端的命令。Windows 直接执行 steam.exe；Linux 可能是 /usr/bin/steam、steam.sh 或 flatpak run。
    /// </summary>
    SteamLaunchCommand GetSteamLaunchCommand() => new(ReadSteamRegistry().SteamExe, null);

    /// <summary>
    /// 数据源页展示用的「注册表」位置描述（Windows 为注册表键，Linux 为 registry.vdf 中的路径）。
    /// </summary>
    string DescribeRegistryKey(SteamRegistryKey key) => key switch
    {
        SteamRegistryKey.Steam => @"HKCU\Software\Valve\Steam",
        SteamRegistryKey.ActiveProcess => @"HKCU\Software\Valve\Steam\ActiveProcess",
        SteamRegistryKey.Apps => @"HKCU\Software\Valve\Steam\Apps",
        _ => throw new ArgumentOutOfRangeException(nameof(key))
    };
}

/// <summary>启动 Steam 客户端的可执行文件与参数（参数为空表示直接执行）。</summary>
public sealed record SteamLaunchCommand(string FileName, string? Arguments);

public enum SteamRegistryKey
{
    Steam,
    ActiveProcess,
    Apps
}

public sealed class SteamRegistrySnapshot
{
    public int AlreadyRetriedOfflineMode { get; init; }
    public string AutoLoginUser { get; init; } = string.Empty;
    public string AutoLoginUserSteamChina { get; init; } = string.Empty;
    public int CompletedOOBEStage1 { get; init; }
    public string Language { get; init; } = string.Empty;
    public string LastGameNameUsed { get; init; } = string.Empty;
    public string PseudoUUID { get; init; } = string.Empty;
    public string Rate { get; init; } = string.Empty;
    public int RememberPassword { get; init; }
    public int Restart { get; init; }
    public int RunningAppID { get; init; }
    public string Skin { get; init; } = string.Empty;
    public string SourceModInstallPath { get; init; } = string.Empty;
    public int StartupModeTmp { get; init; }
    public int StartupModeTmpIsValid { get; init; }
    public string SteamExe { get; init; } = string.Empty;
    public string SteamPath { get; init; } = string.Empty;
    public int SuppressAutoRun { get; init; }
}

public sealed record SteamActiveProcessSnapshot(
    int ActiveUser,
    int Pid,
    string SteamClientDll,
    string SteamClientDll64,
    string Universe);

public sealed record SteamAppRegistrySnapshot(
    int AppId,
    int? Firewall,
    int? FmSysInfo,
    int? Cloud,
    int? Installed,
    string? Name,
    int? Running,
    int? Updating);

public interface IProcessController
{
    IReadOnlyList<IProcessHandle> GetProcessesByNames(IEnumerable<string> processNames);
    bool StopWindowsService(string serviceName);
    IProcessHandle? StartProcess(string filePath, bool useShellExecute = false, string? arguments = null,
        string? workingDirectory = null, IReadOnlyDictionary<string, string>? environment = null);
}

public interface IProcessHandle : IDisposable
{
    string Name { get; }
    int Id { get; }
    bool KillAndWaitForExit();
}

public interface IDesktopActivityProbe
{
    DesktopActivitySample Sample();
}

/// <summary>
/// 一次桌面活动采样。任一字段为 null 表示当前平台 / 会话无法得知（例如 Wayland 不允许读取前台窗口）。
/// </summary>
/// <param name="IdleTime">距最后一次键鼠 / 手柄输入的时长。</param>
/// <param name="ForegroundProcessPath">前台窗口所属进程的可执行文件路径。</param>
/// <param name="ForegroundWorkingDirectory">前台进程的工作目录（Linux 上 Proton 游戏的可执行文件是 Wine 加载器，需要用工作目录匹配安装目录）。</param>
public sealed record DesktopActivitySample(
    TimeSpan? IdleTime,
    string? ForegroundProcessPath,
    string? ForegroundWorkingDirectory = null);
