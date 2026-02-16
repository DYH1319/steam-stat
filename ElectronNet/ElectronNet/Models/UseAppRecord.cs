namespace ElectronNet.Models;

public class UseAppRecord
{
    public int Id { get; init; }

    public int AppId { get; init; }

    public string SteamId { get; init; } = string.Empty;

    public int StartTime { get; init; }

    public int? EndTime { get; set; }

    public int? Duration { get; set; }
}