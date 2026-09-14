using System.Diagnostics;
using System.Diagnostics.Metrics;
using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Core.Http;

internal static class SteamTelemetry
{
    internal static readonly ActivitySource Activities = new("SteamStat.Core.Steam");
    private static readonly Meter Meter = new("SteamStat.Core.Steam");
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>("steam.gateway.requests");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("steam.gateway.failures");
    private static readonly Counter<long> Retries = Meter.CreateCounter<long>("steam.gateway.retries");
    private static readonly Counter<long> RateLimitRejected = Meter.CreateCounter<long>("steam.rate_limit.rejected");
    private static readonly Histogram<double> RateLimitWait = Meter.CreateHistogram<double>("steam.rate_limit.wait_duration", "ms");

    internal static void RecordRequest(string transport, SteamDependency dependency, string operation)
        => Requests.Add(1, Tags(transport, dependency, operation));

    internal static void RecordFailure(
        string transport,
        SteamDependency dependency,
        string operation,
        SteamFailureKind failureKind)
    {
        var tags = Tags(transport, dependency, operation);
        tags.Add("failure.kind", failureKind.ToString());
        Failures.Add(1, tags);
    }

    internal static void RecordRetry(SteamDependency dependency)
        => Retries.Add(1, new KeyValuePair<string, object?>("dependency", dependency.ToString()));

    internal static void RecordRateLimit(
        string transport,
        SteamDependency dependency,
        string operation,
        TimeSpan wait,
        bool rejected)
    {
        var tags = Tags(transport, dependency, operation);
        RateLimitWait.Record(wait.TotalMilliseconds, tags);
        if (rejected) RateLimitRejected.Add(1, tags);
    }

    private static TagList Tags(string transport, SteamDependency dependency, string operation)
    {
        TagList tags = default;
        tags.Add("transport", transport);
        tags.Add("dependency", dependency.ToString());
        tags.Add("operation", operation);
        return tags;
    }
}
