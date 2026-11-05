using Microsoft.Extensions.Logging;
using SteamStat.Core.Platform;
using ValveKeyValue;

namespace SteamStat.Platform.Linux;

/// <summary>
/// Linux / Steam Deck 桌面模式的 Steam 安装定位。
/// <list type="bullet">
/// <item>注册表数据来自 Steam 维护的 registry.vdf（HKCU/Software/Valve/Steam 等）。</item>
/// <item>运行中的游戏以 /proc 中的 reaper 进程为准（registry.vdf 的 Running 标记写盘不及时，且崩溃后会残留）。</item>
/// <item>当前登录账号优先取 ActiveProcess.ActiveUser，缺失时依次取 steamwebhelper 的 -steamid 参数和 connection_log.txt。</item>
/// </list>
/// 本应用以 Flatpak 运行时看不到宿主进程，此时退回 registry.vdf 中的标记。
/// </summary>
internal sealed class LinuxSteamInstallLocator(
    LinuxSystemContext context,
    ILogger<LinuxSteamInstallLocator> logger) : ISteamInstallLocator
{
    private static readonly TimeSpan InstallationCacheDuration = TimeSpan.FromSeconds(15);

    private readonly object _sync = new();
    private readonly ProcScanner _proc = new(context.ProcRoot);
    private LinuxSteamInstallation? _installation;
    private long _installationResolvedAt = long.MinValue;
    private (string Path, DateTime WriteTime, KVDocument? Document)? _registryCache;

    private bool UseProcScan => !context.IsFlatpakSandbox && _proc.IsAvailable;

    public SteamRegistrySnapshot ReadSteamRegistry()
    {
        var installation = GetInstallation();
        if (installation == null) return new SteamRegistrySnapshot();

        var steam = SteamKey(installation);
        var runningAppId = SteamRegistryFile.GetInt(steam, "RunningAppID");
        if (runningAppId == 0 && UseProcScan) runningAppId = _proc.FindRunningAppIds().DefaultIfEmpty(0).Min();

        return new SteamRegistrySnapshot
        {
            AlreadyRetriedOfflineMode = SteamRegistryFile.GetInt(steam, "AlreadyRetriedOfflineMode"),
            AutoLoginUser = SteamRegistryFile.GetString(steam, "AutoLoginUser") ?? string.Empty,
            AutoLoginUserSteamChina = SteamRegistryFile.GetString(steam, "AutoLoginUser_steamchina") ?? string.Empty,
            CompletedOOBEStage1 = SteamRegistryFile.GetInt(steam, "CompletedOOBEStage1"),
            Language = SteamRegistryFile.GetString(steam, "language") ?? string.Empty,
            LastGameNameUsed = SteamRegistryFile.GetString(steam, "LastGameNameUsed") ?? string.Empty,
            PseudoUUID = SteamRegistryFile.GetString(steam, "PseudoUUID") ?? string.Empty,
            Rate = SteamRegistryFile.GetString(steam, "Rate") ?? string.Empty,
            RememberPassword = SteamRegistryFile.GetInt(steam, "RememberPassword"),
            Restart = SteamRegistryFile.GetInt(steam, "Restart"),
            RunningAppID = runningAppId,
            Skin = SteamRegistryFile.GetString(steam, "Skin") ?? string.Empty,
            SourceModInstallPath = (SteamRegistryFile.GetString(steam, "SourceModInstallPath") ?? string.Empty).Replace('\\', '/'),
            StartupModeTmp = SteamRegistryFile.GetInt(steam, "StartupModeTmp"),
            StartupModeTmpIsValid = SteamRegistryFile.GetInt(steam, "StartupModeTmpIsValid"),
            SteamExe = GetSteamLaunchCommand(installation).FileName,
            SteamPath = installation.Root,
            SuppressAutoRun = SteamRegistryFile.GetInt(steam, "SuppressAutoRun")
        };
    }

    public SteamActiveProcessSnapshot ReadActiveProcess()
    {
        var installation = GetInstallation();
        if (installation == null) return new SteamActiveProcessSnapshot(0, 0, string.Empty, string.Empty, string.Empty);

        var document = LoadRegistry(installation);
        var active = SteamRegistryFile.Navigate(document, [.. SteamRegistryFile.SteamKeyPath, "ActiveProcess"]);
        var pid = SteamRegistryFile.GetInt(active, "pid");
        if (pid == 0) pid = SteamRegistryFile.GetInt(SteamRegistryFile.Navigate(document, SteamRegistryFile.MachineSteamKeyPath), "SteamPID");

        // registry.vdf 在 Steam 崩溃后不会清理；新版 Steam 运行时写入的还可能是容器内的 pid。
        // 该 pid 不存在时改按 /proc 查找安装目录下的 steam 客户端进程
        if (UseProcScan && !_proc.IsAlive(pid)) pid = _proc.FindSteamClientPid(installation.Root);

        var activeUser = SteamRegistryFile.GetInt(active, "ActiveUser");
        if (UseProcScan && pid == 0) activeUser = 0;
        else if (activeUser == 0)
        {
            if (UseProcScan) activeUser = _proc.FindLoggedInAccountId();
            // 日志不会在 Steam 退出时清空，只有 Steam 在运行时才代表当前账号
            if (activeUser == 0 && pid != 0) activeUser = SteamConnectionLog.ReadAccountId(SteamConnectionLog.PathIn(installation.Root));
        }

        return new SteamActiveProcessSnapshot(
            activeUser,
            pid,
            SteamRegistryFile.GetString(active, "SteamClientDll") ?? string.Empty,
            SteamRegistryFile.GetString(active, "SteamClientDll64") ?? string.Empty,
            SteamRegistryFile.GetString(active, "Universe") ?? (pid == 0 ? string.Empty : "Public"));
    }

    public IReadOnlyDictionary<int, SteamAppRegistrySnapshot> ReadAppRegistry()
    {
        var result = new Dictionary<int, SteamAppRegistrySnapshot>();
        var installation = GetInstallation();
        if (installation == null) return result;

        var apps = SteamRegistryFile.Child(SteamKey(installation), "Apps");
        if (apps != null)
        {
            foreach (var app in apps.Children)
            {
                if (!int.TryParse(app.Name, out var appId) || app.Value.ValueType != KVValueType.Collection) continue;
                result[appId] = new SteamAppRegistrySnapshot(
                    appId,
                    SteamRegistryFile.GetNullableInt(app, "firewall"),
                    SteamRegistryFile.GetNullableInt(app, "FmSysInfo"),
                    SteamRegistryFile.GetNullableInt(app, "Cloud"),
                    SteamRegistryFile.GetNullableInt(app, "Installed"),
                    SteamRegistryFile.GetString(app, "name"),
                    SteamRegistryFile.GetNullableInt(app, "Running"),
                    SteamRegistryFile.GetNullableInt(app, "Updating"));
            }
        }

        if (!UseProcScan) return result;

        var running = _proc.FindRunningAppIds();
        foreach (var (appId, snapshot) in result.ToArray())
        {
            var isRunning = running.Contains(appId) ? 1 : 0;
            if (snapshot.Running != isRunning) result[appId] = snapshot with { Running = isRunning };
        }
        foreach (var appId in running.Where(appId => !result.ContainsKey(appId)))
            result[appId] = new SteamAppRegistrySnapshot(appId, null, null, null, 1, null, 1, null);
        return result;
    }

    public bool HasRegistryKey(SteamRegistryKey key)
    {
        var installation = GetInstallation();
        if (installation == null) return false;
        var steam = SteamKey(installation);
        return key switch
        {
            SteamRegistryKey.Steam => steam != null,
            // 两者都能从 /proc 推导，不依赖 registry.vdf 是否写入了对应节点
            SteamRegistryKey.ActiveProcess => SteamRegistryFile.Child(steam, "ActiveProcess") != null || (UseProcScan && ReadActiveProcess().Pid != 0),
            SteamRegistryKey.Apps => SteamRegistryFile.Child(steam, "Apps") != null || UseProcScan,
            _ => throw new ArgumentOutOfRangeException(nameof(key))
        };
    }

    public void SetAutoLoginUser(string accountName, bool rememberPassword)
    {
        var installation = GetInstallation();
        if (installation == null || !File.Exists(installation.RegistryFile))
        {
            logger.LogWarning("Steam registry.vdf was not found; the next Steam login user was not changed");
            return;
        }

        lock (_sync)
        {
            var document = SteamRegistryFile.Load(installation.RegistryFile)
                ?? throw new InvalidDataException("Steam registry.vdf could not be parsed");
            var steam = SteamRegistryFile.EnsurePath(document, SteamRegistryFile.SteamKeyPath);
            SteamRegistryFile.SetString(steam, "AutoLoginUser", accountName);
            if (SteamRegistryFile.Child(steam, "AutoLoginUser_steamchina") != null)
                SteamRegistryFile.SetString(steam, "AutoLoginUser_steamchina", accountName);
            SteamRegistryFile.SetString(steam, "RememberPassword", rememberPassword ? "1" : "0");
            SteamRegistryFile.Save(installation.RegistryFile, document);
            _registryCache = null;
        }
    }

    public SteamLaunchCommand GetSteamLaunchCommand()
    {
        var installation = GetInstallation();
        return installation == null ? new SteamLaunchCommand(string.Empty, null) : GetSteamLaunchCommand(installation);
    }

    public string DescribeRegistryKey(SteamRegistryKey key)
    {
        var file = GetInstallation()?.RegistryFile ?? Path.Combine(context.HomeDirectory, ".steam", "registry.vdf");
        var node = key switch
        {
            SteamRegistryKey.Steam => "HKCU/Software/Valve/Steam",
            SteamRegistryKey.ActiveProcess => "HKCU/Software/Valve/Steam/ActiveProcess",
            SteamRegistryKey.Apps => "HKCU/Software/Valve/Steam/Apps",
            _ => throw new ArgumentOutOfRangeException(nameof(key))
        };
        return $"{context.Abbreviate(file)} → {node}";
    }

    internal SteamLaunchCommand GetSteamLaunchCommand(LinuxSteamInstallation installation)
    {
        var command = installation.Flavor switch
        {
            LinuxSteamFlavor.Flatpak => FindExecutable("flatpak") is { } flatpak
                ? new SteamLaunchCommand(flatpak, $"run {LinuxSteamInstallationResolver.FlatpakSteamAppId}")
                : new SteamLaunchCommand("flatpak", $"run {LinuxSteamInstallationResolver.FlatpakSteamAppId}"),
            LinuxSteamFlavor.Snap => new SteamLaunchCommand(FindExecutable("steam") ?? "/snap/bin/steam", null),
            _ => FindExecutable("steam") is { } steam
                ? new SteamLaunchCommand(steam, null)
                : new SteamLaunchCommand(Path.Combine(installation.Root, "steam.sh"), null)
        };

        // 本应用在 Flatpak 沙箱内：需要经 flatpak-spawn 在宿主上启动 Steam
        if (!context.IsFlatpakSandbox) return command;
        var hostCommand = command.FileName.Contains(' ') ? $"\"{command.FileName}\"" : command.FileName;
        return new SteamLaunchCommand("flatpak-spawn", $"--host {hostCommand} {command.Arguments}".TrimEnd());
    }

    private string? FindExecutable(string name)
        => context.ExecutableSearchPaths
            .Select(directory => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);

    private KVObject? SteamKey(LinuxSteamInstallation installation)
        => SteamRegistryFile.Navigate(LoadRegistry(installation), SteamRegistryFile.SteamKeyPath);

    private LinuxSteamInstallation? GetInstallation()
    {
        lock (_sync)
        {
            var now = Environment.TickCount64;
            if (_installationResolvedAt != long.MinValue && now - _installationResolvedAt < InstallationCacheDuration.TotalMilliseconds)
                return _installation;
            _installation = LinuxSteamInstallationResolver.Resolve(context);
            _installationResolvedAt = now;
            return _installation;
        }
    }

    /// <summary>registry.vdf 每次检测都会读取，按修改时间缓存解析结果。</summary>
    private KVDocument? LoadRegistry(LinuxSteamInstallation installation)
    {
        DateTime writeTime;
        try
        {
            if (!File.Exists(installation.RegistryFile)) return null;
            writeTime = File.GetLastWriteTimeUtc(installation.RegistryFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        lock (_sync)
        {
            if (_registryCache is { } cached && cached.Path == installation.RegistryFile && cached.WriteTime == writeTime)
                return cached.Document;
            var document = SteamRegistryFile.Load(installation.RegistryFile);
            if (document == null) logger.LogWarning("Failed to parse Steam registry.vdf");
            _registryCache = (installation.RegistryFile, writeTime, document);
            return document;
        }
    }
}
