using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace ElectronNet.Models;

public class SteamApp
{
    public int Id { get; init; }
    
    public int AppId { get; init; }

    public string? Name { get; init; }

    public string NameLocalizedJson { get; init; } = "{}";

    [NotMapped]
    public Dictionary<string, string> NameLocalized
    {
        get => JsonSerializer.Deserialize<Dictionary<string, string>>(NameLocalizedJson) 
               ?? new Dictionary<string, string>();
        init => NameLocalizedJson = JsonSerializer.Serialize(value);
    }
    
    public bool Installed { get; init; }

    public string? InstallDir { get; init; }

    public string? InstallDirPath { get; init; }

    public long? AppOnDisk { get; init; }

    public long? AppOnDiskReal { get; init; }

    public bool IsRunning { get; init; }

    public string? Type { get; init; }

    public string? Developer { get; init; }

    public string? Publisher { get; init; }

    public int? SteamReleaseDate { get; init; }

    public bool? IsFreeApp { get; init; }

    public int RefreshTime { get; init; }
}