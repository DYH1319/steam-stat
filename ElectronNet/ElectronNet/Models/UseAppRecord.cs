namespace ElectronNet.Models;

public class UseAppRecord
{
    public int Id { get; init; }

    public int AppId { get; init; }

    public long SteamId { get; init; }

    public int StartTime { get; init; }

    public int? EndTime { get; set; }

    public int? Duration { get; set; }
}