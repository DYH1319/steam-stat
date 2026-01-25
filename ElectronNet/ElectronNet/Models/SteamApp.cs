// using System.ComponentModel.DataAnnotations.Schema;
// using System.Text.Json;

namespace ElectronNet.Models;

public class SteamApp
{
    public int Id { get; init; }
    
    public int AppId { get; set; }

    public string? Name { get; set; }

    public string NameLocalizedJson { get; set; } = "{}";

    // [NotMapped]
    // public Dictionary<string, string> NameLocalized
    // {
    //     get => JsonSerializer.Deserialize<Dictionary<string, string>>(NameLocalizedJson) 
    //            ?? new Dictionary<string, string>();
    //     set => NameLocalizedJson = JsonSerializer.Serialize(value);
    // }
    
    public bool Installed { get; set; }

    public string? InstallDir { get; set; }

    public string? InstallDirPath { get; set; }

    public long? AppOnDisk { get; set; }

    public long? AppOnDiskReal { get; set; }

    public bool IsRunning { get; set; }

    public string? Type { get; set; }

    public string? Developer { get; set; }

    public string? Publisher { get; set; }

    public int? SteamReleaseDate { get; set; }

    public bool? IsFreeApp { get; set; }

    public int RefreshTime { get; set; }
}