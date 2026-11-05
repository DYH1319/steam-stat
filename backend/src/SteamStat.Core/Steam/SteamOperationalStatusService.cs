using SteamStat.Core.Http;
using SteamStat.Core.Steam.Cache;
using SteamStat.Core.Steam.Session;

namespace SteamStat.Core.Steam;

public enum SteamConnectivitySummary
{
    Online,
    Degraded,
    Offline
}

public sealed record SteamOperationalStatus(
    SteamConnectivitySummary Connectivity,
    SteamConnectivitySnapshot Dependencies,
    IReadOnlyList<SteamSessionStatusSnapshot> Sessions,
    IReadOnlyList<SteamResourceStatus> Resources,
    IReadOnlyList<string> ReauthenticationAccounts);

public sealed class SteamOperationalStatusService(
    ISteamConnectivityMonitor connectivityMonitor,
    ISteamSessionStatusProvider sessionStatusProvider,
    SteamFeatureSnapshotStore snapshotStore)
{
    public async Task<SteamOperationalStatus> GetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dependencies = connectivityMonitor.Current;
        var sessions = sessionStatusProvider.GetSessionStatuses();
        var resources = await snapshotStore.GetResourceStatusesAsync(cancellationToken).ConfigureAwait(false);
        var health = new[]
        {
            dependencies.CmTransport,
            dependencies.SteamWebApi,
            dependencies.Store,
            dependencies.Cdn,
            // 社区只提供等级和头像框，部分网络下一直不通，不能因此显示连接异常
            dependencies.PublicData,
            dependencies.Download
        };
        var hasAvailableUpstream = sessions.Any(session => session.State == SteamSessionState.Ready)
            || health.Any(item => item.State == DependencyHealthState.Healthy);
        var hasDegradedUpstream = health.Any(item => item.State is DependencyHealthState.Degraded
            or DependencyHealthState.Unavailable);
        var summary = !hasAvailableUpstream
            ? SteamConnectivitySummary.Offline
            : hasDegradedUpstream
                ? SteamConnectivitySummary.Degraded
                : SteamConnectivitySummary.Online;
        return new SteamOperationalStatus(
            summary,
            dependencies,
            sessions,
            resources,
            sessions.Where(session => session.State == SteamSessionState.ReauthenticationRequired)
                .Select(session => session.AccountName)
                .OrderBy(account => account, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }
}
