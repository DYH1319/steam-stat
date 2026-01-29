using System.ComponentModel.DataAnnotations.Schema;

namespace ElectronNet.Models;

public class UseAppRecord
{
    public int Id { get; init; }

    public int AppId { get; init; }

    public long SteamId { get; init; }

    [NotMapped] public string SteamIdStr => SteamId.ToString();

    public int StartTime { get; init; }

    public int? EndTime { get; set; }

    public int? Duration { get; set; }
}