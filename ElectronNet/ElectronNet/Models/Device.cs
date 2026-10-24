namespace ElectronNet.Models;

public class Device
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool IsCurrent { get; set; }

    public int CreatedAt { get; set; }
}
