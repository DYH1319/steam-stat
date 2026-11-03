namespace SteamStat.Platform.Linux;

/// <summary>Steam 在 Linux 上的安装形态。</summary>
internal enum LinuxSteamFlavor
{
    /// <summary>发行版软件包或 Valve 官方 bootstrap（~/.steam、~/.local/share/Steam）。Steam Deck 桌面模式也属于此类。</summary>
    Native,
    /// <summary>Flathub 的 com.valvesoftware.Steam（数据在 ~/.var/app/com.valvesoftware.Steam）。</summary>
    Flatpak,
    /// <summary>Snap 商店版（数据在 ~/snap/steam/common）。</summary>
    Snap
}

/// <summary>一次解析出的 Steam 安装：数据根目录与 registry.vdf 位置。</summary>
internal sealed record LinuxSteamInstallation(string Root, string RegistryFile, LinuxSteamFlavor Flavor);

internal static class LinuxSteamInstallationResolver
{
    internal const string FlatpakSteamAppId = "com.valvesoftware.Steam";

    /// <summary>
    /// 依次检查原生 / Flatpak / Snap 三种安装。多处都存在时取 registry.vdf 最近修改的一处（即最近运行过的 Steam）。
    /// </summary>
    internal static LinuxSteamInstallation? Resolve(LinuxSystemContext context)
    {
        var home = context.HomeDirectory;
        var dataHome = context.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg && Path.IsPathRooted(xdg)
            ? xdg
            : Path.Combine(home, ".local", "share");

        var candidates = new List<LinuxSteamInstallation>();
        AddCandidate(candidates, LinuxSteamFlavor.Native,
            Path.Combine(home, ".steam"),
            [Path.Combine(home, ".steam", "steam"), Path.Combine(home, ".steam", "root"), Path.Combine(dataHome, "Steam"), Path.Combine(home, ".steam", "debian-installation")]);

        var flatpakHome = Path.Combine(home, ".var", "app", FlatpakSteamAppId);
        AddCandidate(candidates, LinuxSteamFlavor.Flatpak,
            Path.Combine(flatpakHome, ".steam"),
            [Path.Combine(flatpakHome, ".steam", "steam"), Path.Combine(flatpakHome, ".local", "share", "Steam")]);

        var snapHome = Path.Combine(home, "snap", "steam", "common");
        AddCandidate(candidates, LinuxSteamFlavor.Snap,
            Path.Combine(snapHome, ".steam"),
            [Path.Combine(snapHome, ".steam", "steam"), Path.Combine(snapHome, ".local", "share", "Steam")]);

        return candidates
            .OrderByDescending(candidate => File.Exists(candidate.RegistryFile) ? File.GetLastWriteTimeUtc(candidate.RegistryFile) : DateTime.MinValue)
            .FirstOrDefault();
    }

    private static void AddCandidate(
        List<LinuxSteamInstallation> candidates,
        LinuxSteamFlavor flavor,
        string steamDotDirectory,
        IEnumerable<string> rootCandidates)
    {
        var root = rootCandidates.Select(ResolveDirectory).FirstOrDefault(IsSteamRoot);
        if (root == null) return;
        candidates.Add(new LinuxSteamInstallation(root, Path.Combine(steamDotDirectory, "registry.vdf"), flavor));
    }

    /// <summary>~/.steam/steam 与 ~/.steam/root 通常是指向真实目录的符号链接，解析到最终目标。</summary>
    private static string? ResolveDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return null;
            var target = Directory.ResolveLinkTarget(path, returnFinalTarget: true);
            return Path.TrimEndingDirectorySeparator(target?.FullName ?? Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsSteamRoot(string? path)
        => path != null
           && (Directory.Exists(Path.Combine(path, "steamapps")) || File.Exists(Path.Combine(path, "config", "config.vdf")));
}
