namespace SteamStat.Platform.Linux;

/// <summary>
/// Linux 平台实现读取的外部环境：家目录、/proc 根与环境变量。
/// 生产环境取当前进程的真实值；测试用夹具目录替换，避免依赖本机 Steam。
/// </summary>
public sealed class LinuxSystemContext
{
    public required string HomeDirectory { get; init; }

    /// <summary>procfs 挂载点，默认 /proc。</summary>
    public string ProcRoot { get; init; } = "/proc";

    /// <summary>系统级可执行文件的查找目录（用于定位 steam / flatpak 启动器）。</summary>
    public IReadOnlyList<string> ExecutableSearchPaths { get; init; } = ["/usr/bin", "/usr/games", "/usr/local/bin", "/snap/bin"];

    public Func<string, string?> GetEnvironmentVariable { get; init; } = Environment.GetEnvironmentVariable;

    /// <summary>本应用自身是否运行在 Flatpak 沙箱内（沙箱内看不到宿主进程，启动宿主程序需经 flatpak-spawn）。</summary>
    public bool IsFlatpakSandbox => !string.IsNullOrEmpty(GetEnvironmentVariable("FLATPAK_ID"));

    public static LinuxSystemContext Current() => new()
    {
        HomeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
    };

    /// <summary>把家目录前缀缩写为 ~，用于界面展示与诊断信息。</summary>
    public string Abbreviate(string path)
        => HomeDirectory.Length > 1 && path.StartsWith(HomeDirectory, StringComparison.Ordinal)
            ? "~" + path[HomeDirectory.Length..]
            : path;
}
