namespace SteamStat.Core.Steam.Gateway;

public enum SteamDataSource
{
    Memory,
    Sqlite,
    PublicData,
    Cm,
    Http
}

public enum SteamFreshness
{
    Fresh,
    Stale,
    Expired
}

public enum SteamFailureKind
{
    Offline,
    AuthenticationRequired,
    Forbidden,
    NotFound,
    RateLimited,
    Transient,
    Timeout,
    Protocol,
    InvalidData,
    Unknown
}

public enum SteamRefreshMode
{
    PreferCache,
    RequireRefresh,
    CacheOnly
}

public sealed record SteamFailure(SteamFailureKind Kind, string DiagnosticCode);

public sealed record SteamGatewayResult<T>(
    bool IsSuccess,
    T? Value,
    SteamDataSource? Source,
    SteamFreshness? Freshness,
    DateTimeOffset? FetchedAt,
    DateTimeOffset? RefreshAfter,
    SteamFailureKind? Failure,
    string? DiagnosticCode = null)
{
    public static SteamGatewayResult<T> Succeeded(
        T? value,
        SteamDataSource source,
        SteamFreshness freshness,
        DateTimeOffset fetchedAt,
        DateTimeOffset refreshAfter,
        SteamFailureKind? failure = null,
        string? diagnosticCode = null)
        => new(true, value, source, freshness, fetchedAt, refreshAfter, failure, diagnosticCode);

    public static SteamGatewayResult<T> Failed(SteamFailureKind failure, string diagnosticCode)
        => new(false, default, null, null, null, null, failure, diagnosticCode);

    public static SteamGatewayResult<T> Failed(SteamFailure failure)
        => Failed(failure.Kind, failure.DiagnosticCode);
}
