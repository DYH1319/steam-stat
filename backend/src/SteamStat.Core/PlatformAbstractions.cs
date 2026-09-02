namespace SteamStat.Core.Platform;

public interface ISecretStore
{
    string? Protect(string? plainText);
    string? Unprotect(string? protectedText);
    bool IsProtected(string? value);
}

public interface ISteamInstallLocator
{
    SteamRegistrySnapshot ReadSteamRegistry();
    SteamActiveProcessSnapshot ReadActiveProcess();
    IReadOnlyDictionary<int, SteamAppRegistrySnapshot> ReadAppRegistry();
    void SetAutoLoginUser(string accountName, bool rememberPassword);
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
