using System.Runtime.InteropServices;

namespace ElectronNet.Hosting;

/// <summary>与运行平台相关、但不属于 Steam 数据层的 Host 细节：系统描述、Electron 沙箱参数、图标与自启动。</summary>
internal static class PlatformRuntime
{
    public static string PlatformName
        => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    public static string Arch => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

    /// <summary>Linux 优先取 /etc/os-release 的 PRETTY_NAME（如 "SteamOS"、"Arch Linux"）。</summary>
    public static string OsDescription()
    {
        if (OperatingSystem.IsLinux())
        {
            var prettyName = ReadOsRelease("/etc/os-release", "PRETTY_NAME");
            if (!string.IsNullOrWhiteSpace(prettyName)) return $"{prettyName} (Linux {Environment.OSVersion.Version.ToString(3)})";
        }
        return RuntimeInformation.OSDescription;
    }

    /// <summary>Linux 图形会话类型（x11 / wayland / tty）；其他平台为 null。</summary>
    public static string? SessionType()
    {
        if (!OperatingSystem.IsLinux()) return null;
        var type = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        if (!string.IsNullOrWhiteSpace(type)) return type.ToLowerInvariant();
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) ? "wayland"
            : !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) ? "x11"
            : null;
    }

    /// <summary>窗口 / 托盘 / 通知图标。Linux 的托盘与通知不支持 .ico。</summary>
    public static string? IconPath()
    {
        var resources = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources");
        var path = Path.Combine(resources, OperatingSystem.IsWindows() ? "icons8-steam-256.ico" : "icons8-steam-256.png");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Linux 上 Chromium 沙箱需要 SUID 的 chrome-sandbox 或允许非特权用户命名空间。
    /// tar.gz 解压与 AppImage 挂载都没有 SUID，Ubuntu 23.10+ 又默认限制用户命名空间，此时 Electron 会直接崩溃，
    /// 只能以 --no-sandbox 启动。设置 STEAM_STAT_NO_SANDBOX=1 可强制关闭沙箱。
    /// </summary>
    public static string ElectronSandboxArguments(string baseDirectory)
    {
        if (!OperatingSystem.IsLinux()) return string.Empty;
        if (Environment.GetEnvironmentVariable("STEAM_STAT_NO_SANDBOX") is "1" or "true") return "--no-sandbox";

        var helper = new[]
            {
                Path.Combine(baseDirectory, "electron", "chrome-sandbox"),
                Path.Combine(baseDirectory, ".electron", "node_modules", "electron", "dist", "chrome-sandbox")
            }
            .FirstOrDefault(File.Exists);
        if (helper != null && (File.GetUnixFileMode(helper) & UnixFileMode.SetUser) != 0) return string.Empty;
        return UserNamespacesRestricted() ? "--no-sandbox" : string.Empty;
    }

    private static bool UserNamespacesRestricted()
        => ReadProcValue("/proc/sys/kernel/unprivileged_userns_clone") == "0"
           || ReadProcValue("/proc/sys/user/max_user_namespaces") == "0"
           || ReadProcValue("/proc/sys/kernel/apparmor_restrict_unprivileged_userns") == "1";

    private static string? ReadProcValue(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string? ReadOsRelease(string path, string key)
    {
        try
        {
            if (!File.Exists(path)) return null;
            foreach (var line in File.ReadLines(path))
            {
                if (!line.StartsWith(key + "=", StringComparison.Ordinal)) continue;
                return line[(key.Length + 1)..].Trim().Trim('"', '\'');
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }
}

/// <summary>
/// Linux 开机自启：写入 XDG autostart 目录（~/.config/autostart/steam-stat.desktop）。
/// Electron 的 setLoginItemSettings 在 Linux 上不生效。
/// </summary>
internal static class LinuxAutoStart
{
    internal const string FileName = "steam-stat.desktop";

    public static string AutostartFile(Func<string, string?> getEnvironmentVariable, string homeDirectory)
    {
        var configHome = getEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } configured && Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(homeDirectory, ".config");
        return Path.Combine(configHome, "autostart", FileName);
    }

    /// <summary>
    /// 图标：AppImage 每次运行挂载到不同的临时目录、Flatpak 会导出图标主题，二者都用图标名；
    /// tar.gz 等没有安装图标，写程序目录内图标的绝对路径。
    /// </summary>
    public static string? IconFor(AppDistribution distribution)
        => distribution.Kind is "appimage" or "flatpak" or "aur" ? null : PlatformRuntime.IconPath();

    /// <summary>自启动命令：AppImage 用镜像文件本身，Flatpak 用 flatpak run，其余用当前 .NET 可执行文件。</summary>
    public static IReadOnlyList<string> LaunchCommand(AppDistribution distribution, Func<string, string?> getEnvironmentVariable, string? processPath)
    {
        if (distribution.Kind == "flatpak")
            return ["flatpak", "run", getEnvironmentVariable("FLATPAK_ID") ?? "asia.dyh1319.steamstat"];
        if (getEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage) return [appImage];
        return processPath == null ? [] : [processPath];
    }

    public static string BuildDesktopEntry(IReadOnlyList<string> command, bool silentStart, string? iconPath = null)
    {
        var arguments = silentStart ? command.Append("--silent-start") : command;
        return string.Join('\n',
            "[Desktop Entry]",
            "Type=Application",
            "Name=Steam Stat",
            "Comment=Steam data archive",
            $"Exec={string.Join(' ', arguments.Select(QuoteExecArgument))}",
            // AppImage / tar.gz 没有安装到图标主题，直接写图标的绝对路径
            $"Icon={iconPath ?? "steam-stat"}",
            "Terminal=false",
            "X-GNOME-Autostart-enabled=true",
            "");
    }

    public static void Apply(string autostartFile, IReadOnlyList<string> command, bool autoStart, bool silentStart, string? iconPath)
    {
        if (!autoStart || command.Count == 0)
        {
            if (File.Exists(autostartFile)) File.Delete(autostartFile);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(autostartFile)!);
        File.WriteAllText(autostartFile, BuildDesktopEntry(command, silentStart, iconPath));
    }

    /// <summary>Desktop Entry 规范：含空格或保留字符的参数用双引号包裹，并转义 " ` $ \。</summary>
    internal static string QuoteExecArgument(string argument)
    {
        if (argument.Length > 0 && argument.All(c => char.IsAsciiLetterOrDigit(c) || "-_./=:+,@".Contains(c))) return argument;
        var escaped = argument.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$");
        return $"\"{escaped.Replace("%", "%%")}\"";
    }
}
