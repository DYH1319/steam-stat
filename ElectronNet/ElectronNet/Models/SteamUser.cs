namespace ElectronNet.Models;

public class SteamUser
{
    public int Id { get; init; }
    
    public long SteamId { get; init; }
    
    public int AccountId { get; init; }
    
    public string AccountName { get; init; } = string.Empty;

    public string? PersonaName { get; init; }

    public bool? RememberPassword { get; init; }

    public bool? WantsOfflineMode { get; init; }

    public bool? SkipOfflineModeWarning { get; init; }

    public bool? AllowAutoLogin { get; init; }

    public bool? MostRecent { get; init; }

    public int? Timestamp { get; init; }

    public string? AvatarFull { get; init; }

    public string? AvatarMedium { get; init; }

    public string? AvatarSmall { get; init; }

    public string? AnimatedAvatar { get; init; }

    public string? AvatarFrame { get; init; }

    public int? Level { get; init; }

    public string? LevelClass { get; init; }
}