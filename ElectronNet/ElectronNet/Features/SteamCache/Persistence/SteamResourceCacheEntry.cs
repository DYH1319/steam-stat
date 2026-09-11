namespace ElectronNet.Features.SteamCache.Persistence;

public sealed class SteamResourceCacheEntry
{
    public long Id { get; init; }
    public string ResourceKind { get; set; } = string.Empty;
    public string ScopeId { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string Variant { get; set; } = string.Empty;
    public int SchemaVersion { get; set; }
    public string PayloadFormat { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string? ETag { get; set; }
    public string? ContentHash { get; set; }
    public long FetchedAt { get; set; }
    public long RefreshAfter { get; set; }
    public long? RetainUntil { get; set; }
    public long LastAccessedAt { get; set; }
}
