namespace ElectronNet.Models;

public class UseAppRecord
{
    public int Id { get; init; }

    public int AppId { get; init; }

    public string SteamId { get; init; } = string.Empty;

    public int StartTime { get; set; }

    public int? EndTime { get; set; }

    public int? Duration { get; set; }

    public int? LastHeartbeat { get; set; }

    public long IdleDuration { get; set; }

    public long? ForegroundDuration { get; set; }

    public string? DeviceId { get; set; }

    public string Source { get; set; } = "auto";

    public string? EndReason { get; set; }

    public string? Note { get; set; }

    public string? Tags { get; set; }

    public int? UpdatedAt { get; set; }
}