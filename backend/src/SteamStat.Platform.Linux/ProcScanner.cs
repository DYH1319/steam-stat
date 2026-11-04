using System.Globalization;

namespace SteamStat.Platform.Linux;

/// <summary>
/// 只读扫描 procfs。读不到的进程（已退出、属于其他用户）直接跳过。
/// </summary>
internal sealed class ProcScanner(string procRoot)
{
    /// <summary>SteamID64 的个人账号基数：AccountId = SteamID64 - 76561197960265728。</summary>
    private const long SteamId64Base = 76561197960265728;

    public bool IsAvailable => Directory.Exists(Path.Combine(procRoot, "self")) || Directory.Exists(procRoot);

    public IEnumerable<int> ProcessIds()
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateDirectories(procRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var entry in entries)
        {
            if (int.TryParse(Path.GetFileName(entry), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                yield return pid;
        }
    }

    public bool IsAlive(int pid) => pid > 0 && Directory.Exists(Path.Combine(procRoot, pid.ToString(CultureInfo.InvariantCulture)));

    public string? ReadComm(int pid) => ReadText(pid, "comm")?.TrimEnd('\n');

    public IReadOnlyList<string> ReadCommandLine(int pid)
    {
        var raw = ReadText(pid, "cmdline");
        return string.IsNullOrEmpty(raw) ? [] : raw.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    public string? ReadExecutablePath(int pid) => ReadLink(pid, "exe");

    public string? ReadWorkingDirectory(int pid) => ReadLink(pid, "cwd");

    /// <summary>
    /// Steam 启动的每个游戏都由 reaper 包装：<c>reaper SteamLaunch AppId=&lt;id&gt; -- …</c>。
    /// 非 Steam 快捷方式的 AppId 高位为 1（超出 int 范围），自然被排除。
    /// </summary>
    public IReadOnlySet<int> FindRunningAppIds()
    {
        var result = new HashSet<int>();
        foreach (var pid in ProcessIds())
        {
            if (ReadComm(pid) != "reaper") continue;
            var appId = ParseReaperAppId(ReadCommandLine(pid));
            if (appId is > 0) result.Add(appId.Value);
        }
        return result;
    }

    internal static int? ParseReaperAppId(IReadOnlyList<string> commandLine)
    {
        if (!commandLine.Contains("SteamLaunch", StringComparer.Ordinal)) return null;
        foreach (var argument in commandLine)
        {
            if (argument == "--") break;
            if (argument.StartsWith("AppId=", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(argument.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out var appId)
                && appId is > 0 and <= int.MaxValue)
            {
                return (int)appId;
            }
        }
        return null;
    }

    /// <summary>
    /// 已登录账号：Steam 的 steamwebhelper 以 <c>-steamid=&lt;SteamID64&gt;</c> 启动，未登录时为 0。
    /// </summary>
    public int FindLoggedInAccountId()
    {
        foreach (var pid in ProcessIds())
        {
            if (ReadComm(pid) != "steamwebhelper") continue;
            var accountId = ParseWebHelperAccountId(ReadCommandLine(pid));
            if (accountId > 0) return accountId;
        }
        return 0;
    }

    internal static int ParseWebHelperAccountId(IReadOnlyList<string> commandLine)
    {
        foreach (var argument in commandLine)
        {
            var value = argument.StartsWith("-steamid=", StringComparison.Ordinal) ? argument[9..]
                : argument.StartsWith("--steamid=", StringComparison.Ordinal) ? argument[10..]
                : null;
            if (value == null) continue;
            if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var steamId) && steamId > SteamId64Base)
            {
                var accountId = steamId - SteamId64Base;
                if (accountId is > 0 and <= uint.MaxValue) return unchecked((int)(uint)accountId);
            }
        }
        return 0;
    }

    /// <summary>
    /// Steam 客户端主进程：comm 为 steam 且可执行文件位于 Steam 安装目录下（排除 /usr/bin/steam 启动器等同名进程）。
    /// 有多个时取 pid 最大者（steamrt 启动器会先起一个 steam 再启动真正的客户端）。
    /// </summary>
    public int FindSteamClientPid(string steamRoot)
    {
        var prefix = Path.TrimEndingDirectorySeparator(steamRoot) + Path.DirectorySeparatorChar;
        return ProcessIds()
            .Where(pid => ReadComm(pid) == "steam"
                          && ReadExecutablePath(pid) is { } exe
                          && exe.StartsWith(prefix, StringComparison.Ordinal))
            .DefaultIfEmpty(0)
            .Max();
    }

    /// <summary>按 comm 名称查找进程（comm 最长 15 字符，与 Steam 进程名一致）。</summary>
    public IReadOnlyList<int> FindByComm(IReadOnlyCollection<string> names)
        => ProcessIds().Where(pid => ReadComm(pid) is { } comm && names.Contains(comm)).ToArray();

    private string? ReadText(int pid, string name)
    {
        try
        {
            return File.ReadAllText(Path.Combine(procRoot, pid.ToString(CultureInfo.InvariantCulture), name));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string? ReadLink(int pid, string name)
    {
        try
        {
            var path = Path.Combine(procRoot, pid.ToString(CultureInfo.InvariantCulture), name);
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) return new FileInfo(path).LinkTarget;
            if ((attributes & FileAttributes.Directory) != 0) return null;
            // 测试夹具用小文本文件模拟符号链接（内容即目标路径）；绝不读取真实 /proc/<pid>/exe 指向的二进制
            return new FileInfo(path).Length <= 4096 ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
