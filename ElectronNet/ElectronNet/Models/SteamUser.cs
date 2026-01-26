using System.ComponentModel.DataAnnotations.Schema;

namespace ElectronNet.Models;

public class SteamUser
{
    public int Id { get; init; }

    public long SteamId { get; set; }

    [NotMapped] public string SteamIdStr => SteamId.ToString();

    public int AccountId { get; set; }

    public string AccountName { get; set; } = string.Empty;

    public string? PersonaName { get; set; }

    public bool? RememberPassword { get; set; }

    public bool? WantsOfflineMode { get; set; }

    public bool? SkipOfflineModeWarning { get; set; }

    public bool? AllowAutoLogin { get; set; }

    public bool? MostRecent { get; set; }

    public int? Timestamp { get; set; }

    public string? AvatarFull { get; set; }

    public string? AvatarMedium { get; set; }

    public string? AvatarSmall { get; set; }

    public string? AnimatedAvatar { get; set; }

    public string? AvatarFrame { get; set; }

    public int? Level { get; set; }

    public string? LevelClass { get; set; }
}