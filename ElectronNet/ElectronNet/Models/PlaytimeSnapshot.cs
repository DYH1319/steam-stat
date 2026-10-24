namespace ElectronNet.Models;

public class PlaytimeSnapshot
{
    public int Id { get; init; }

    public string SteamId { get; set; } = string.Empty;

    public int AppId { get; set; }

    public string Source { get; set; } = "localconfig";

    public int PlaytimeMinutes { get; set; }

    public int CapturedAt { get; set; }

    public int LastSeenAt { get; set; }

    public int? LastPlayed { get; set; }

    public bool IsBaseline { get; set; }
}
