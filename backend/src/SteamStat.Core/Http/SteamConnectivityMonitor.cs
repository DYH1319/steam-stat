using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Core.Http;

public enum SteamDependency
{
    CmTransport,
    SteamWebApi,
    Store,
    Cdn,
    Community,
    PublicData,
    Download
}

public enum DependencyHealthState
{
    Unknown,
    Healthy,
    Degraded,
    Unavailable
}

public sealed record DependencyHealth(
    DependencyHealthState State,
    DateTimeOffset? LastSuccessAt = null,
    DateTimeOffset? LastFailureAt = null,
    SteamFailureKind? LastFailureKind = null,
    string? DiagnosticCode = null,
    bool IsCircuitOpen = false,
    bool IsRateLimited = false);

public sealed record SteamConnectivitySnapshot(
    DependencyHealth CmTransport,
    DependencyHealth SteamWebApi,
    DependencyHealth Store,
    DependencyHealth Cdn,
    DependencyHealth Community,
    DependencyHealth PublicData,
    DependencyHealth Download,
    DateTimeOffset ChangedAt);

public interface ISteamConnectivityMonitor
{
    SteamConnectivitySnapshot Current { get; }
    void ReportSuccess(SteamDependency dependency);
    void ReportFailure(
        SteamDependency dependency,
        SteamFailure failure,
        bool isCircuitOpen = false,
        bool isRateLimited = false);
}

public sealed class SteamConnectivityMonitor : ISteamConnectivityMonitor
{
    private static readonly DependencyHealth Unknown = new(DependencyHealthState.Unknown);
    private readonly SteamAccessOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private readonly Dictionary<SteamDependency, DependencyHealth> _health = [];
    private DateTimeOffset _changedAt;

    public SteamConnectivityMonitor(SteamAccessOptions options, TimeProvider timeProvider)
    {
        _options = options;
        _timeProvider = timeProvider;
        _changedAt = timeProvider.GetUtcNow();
    }

    public SteamConnectivitySnapshot Current
    {
        get
        {
            lock (_sync)
            {
                var now = _timeProvider.GetUtcNow();
                return new SteamConnectivitySnapshot(
                    GetHealth(SteamDependency.CmTransport, now),
                    GetHealth(SteamDependency.SteamWebApi, now),
                    GetHealth(SteamDependency.Store, now),
                    GetHealth(SteamDependency.Cdn, now),
                    GetHealth(SteamDependency.Community, now),
                    GetHealth(SteamDependency.PublicData, now),
                    GetHealth(SteamDependency.Download, now),
                    _changedAt);
            }
        }
    }

    public void ReportSuccess(SteamDependency dependency)
    {
        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            _health.TryGetValue(dependency, out var previous);
            _health[dependency] = new DependencyHealth(
                DependencyHealthState.Healthy,
                now,
                previous?.LastFailureAt,
                previous?.LastFailureKind,
                previous?.DiagnosticCode);
            if (previous?.State != DependencyHealthState.Healthy) _changedAt = now;
        }
    }

    public void ReportFailure(
        SteamDependency dependency,
        SteamFailure failure,
        bool isCircuitOpen = false,
        bool isRateLimited = false)
    {
        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            _health.TryGetValue(dependency, out var previous);
            var state = failure.Kind is SteamFailureKind.Offline
                ? DependencyHealthState.Unavailable
                : DependencyHealthState.Degraded;
            _health[dependency] = new DependencyHealth(
                state,
                previous?.LastSuccessAt,
                now,
                failure.Kind,
                failure.DiagnosticCode,
                isCircuitOpen,
                isRateLimited);
            if (previous?.State != state
                || previous?.IsCircuitOpen != isCircuitOpen
                || previous?.IsRateLimited != isRateLimited)
                _changedAt = now;
        }
    }

    private DependencyHealth GetHealth(SteamDependency dependency, DateTimeOffset now)
    {
        if (!_health.TryGetValue(dependency, out var health)) return Unknown;
        var evidenceAt = health.LastFailureAt.HasValue
            && (!health.LastSuccessAt.HasValue || health.LastFailureAt > health.LastSuccessAt)
            ? health.LastFailureAt
            : health.LastSuccessAt;
        return evidenceAt.HasValue && now - evidenceAt.Value <= _options.HealthEvidenceLifetime
            ? health
            : health with { State = DependencyHealthState.Unknown, IsCircuitOpen = false, IsRateLimited = false };
    }
}
