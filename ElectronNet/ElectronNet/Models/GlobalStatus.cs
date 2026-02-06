using System.ComponentModel.DataAnnotations.Schema;

namespace ElectronNet.Models;

public class GlobalStatus
{
    public int Id { get; init; } = 1;

    public string? SteamPath { get; init; }

    public string? SteamExePath { get; init; }

    public int? SteamPid { get; init; }

    public string? SteamClientDllPath { get; init; }

    public string? SteamClientDll64Path { get; init; }

    public long? ActiveUserSteamId { get; init; }

    [NotMapped] public string? ActiveUserSteamIdStr => ActiveUserSteamId?.ToString();

    public int? RunningAppId { get; init; }

    public int RefreshTime { get; init; }

    public int? SteamUserRefreshTime { get; set; }
    
    public int? SteamAppRefreshTime { get; set; }
}