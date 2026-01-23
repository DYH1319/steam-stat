using System.Text.Json.Serialization;

// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace ElectronNet.Models.Settings;

public class AppSettings
{
    public static readonly AppSettings DefaultSettings = new()
    {
        AutoStart = false,
        SilentStart = false,
        AutoUpdate = true,
        Language = Program.Locale,
        CloseAction = "ask",
        UpdateAppRunningStatusJob = new UpdateAppRunningStatusJob
        {
            Enabled = true,
            IntervalSeconds = 5
        }
    };

    [JsonPropertyName("autoStart")] public bool? AutoStart { get; set; }

    [JsonPropertyName("slientStart")] public bool? SilentStart { get; set; }

    [JsonPropertyName("autoUpdate")] public bool? AutoUpdate { get; set; }

    [JsonPropertyName("language")] public string? Language { get; set; }

    [JsonPropertyName("closeAction")] public string? CloseAction { get; set; }

    [JsonPropertyName("updateAppRunningStatusJob")]
    public UpdateAppRunningStatusJob? UpdateAppRunningStatusJob { get; set; }
}

public class UpdateAppRunningStatusJob
{
    [JsonPropertyName("enabled")] public bool? Enabled { get; set; }

    [JsonPropertyName("intervalSeconds")] public int? IntervalSeconds { get; set; }
}