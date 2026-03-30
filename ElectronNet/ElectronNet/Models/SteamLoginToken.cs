namespace ElectronNet.Models;

public class SteamLoginToken
{
    public int Id { get; init; }

    public string AccountName { get; set; } = string.Empty;

    public string AccessToken { get; set; } = string.Empty;

    public string RefreshToken { get; set; } = string.Empty;

    public string? GuardData { get; set; }

    public int CreatedAt { get; set; }
}
