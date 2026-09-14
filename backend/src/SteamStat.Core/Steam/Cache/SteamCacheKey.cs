namespace SteamStat.Core.Steam.Cache;

public sealed record SteamCacheKey
{
    private SteamCacheKey(
        string resourceKind,
        string scopeId,
        string resourceId,
        string language,
        string variant,
        int schemaVersion)
    {
        ResourceKind = resourceKind;
        ScopeId = scopeId;
        ResourceId = resourceId;
        Language = language;
        Variant = variant;
        SchemaVersion = schemaVersion;
    }

    public string ResourceKind { get; }
    public string ScopeId { get; }
    public string ResourceId { get; }
    public string Language { get; }
    public string Variant { get; }
    public int SchemaVersion { get; }

    public static SteamCacheKey Create(
        string resourceKind,
        string scopeId,
        string resourceId,
        string language = "",
        string variant = "",
        int schemaVersion = 1)
    {
        resourceKind = NormalizeRequired(resourceKind, nameof(resourceKind), true);
        scopeId = NormalizeRequired(scopeId, nameof(scopeId), true);
        resourceId = NormalizeRequired(resourceId, nameof(resourceId), false);
        if (schemaVersion <= 0) throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        return new SteamCacheKey(
            resourceKind,
            scopeId,
            resourceId,
            NormalizeOptional(language),
            NormalizeOptional(variant),
            schemaVersion);
    }

    private static string NormalizeRequired(string value, string parameterName, bool lowerCase)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length == 0) throw new ArgumentException("Cache key dimensions cannot be empty.", parameterName);
        return lowerCase ? normalized.ToLowerInvariant() : normalized;
    }

    private static string NormalizeOptional(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;
}
