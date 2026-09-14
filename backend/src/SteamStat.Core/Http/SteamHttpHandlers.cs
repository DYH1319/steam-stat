using System.Diagnostics;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Steam.Gateway;
using SteamStat.Core.Steam.Session;

namespace SteamStat.Core.Http;

public static class SteamHttpRequestOptions
{
    private static readonly HttpRequestOptionsKey<string> OperationKey = new("SteamStat.Operation");

    public static void SetOperation(HttpRequestMessage request, string operation)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        request.Options.Set(OperationKey, operation);
    }

    internal static string GetOperation(HttpRequestMessage request)
        => request.Options.TryGetValue(OperationKey, out var operation) ? operation : "other";
}

public sealed class SteamRateLimiterRejectedException(string message) : Exception(message);

internal sealed class SteamHttpRequestQuota : IDisposable
{
    private readonly PartitionedRateLimiter<HttpRequestMessage> _requestLimiter;
    private readonly PartitionedRateLimiter<HttpRequestMessage> _downloadLimiter;

    public SteamHttpRequestQuota(SteamAccessOptions options)
    {
        _requestLimiter = PartitionedRateLimiter.Create<HttpRequestMessage, HttpQuotaPartition>(request =>
            RateLimitPartition.GetTokenBucketLimiter(Partition(request), _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = options.RequestTokenLimit,
                TokensPerPeriod = options.RequestTokensPerPeriod,
                ReplenishmentPeriod = options.RequestReplenishmentPeriod,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = options.HttpQueueLimit,
                AutoReplenishment = true
            }));
        _downloadLimiter = PartitionedRateLimiter.Create<HttpRequestMessage, HttpQuotaPartition>(request =>
            RateLimitPartition.GetConcurrencyLimiter(Partition(request), partition => new ConcurrencyLimiterOptions
            {
                PermitLimit = partition.Dependency == SteamDependency.Cdn
                    ? options.CdnConcurrencyLimit
                    : options.HttpConcurrencyLimit,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = partition.Dependency == SteamDependency.Cdn
                    ? options.CdnQueueLimit
                    : options.HttpQueueLimit
            }));
    }

    public ValueTask<RateLimitLease> AcquireAsync(
        SteamDependency dependency,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Options.Set(DependencyKey, dependency);
        return IsDownload(dependency)
            ? _downloadLimiter.AcquireAsync(request, 1, cancellationToken)
            : _requestLimiter.AcquireAsync(request, 1, cancellationToken);
    }

    public void Dispose()
    {
        _requestLimiter.Dispose();
        _downloadLimiter.Dispose();
    }

    private static readonly HttpRequestOptionsKey<SteamDependency> DependencyKey = new("SteamStat.Dependency");

    private static bool IsDownload(SteamDependency dependency)
        => dependency is SteamDependency.Cdn or SteamDependency.Download;

    private static HttpQuotaPartition Partition(HttpRequestMessage request)
    {
        request.Options.TryGetValue(DependencyKey, out var dependency);
        return new HttpQuotaPartition(
            dependency,
            request.RequestUri?.Authority.ToLowerInvariant() ?? "unknown",
            SteamHttpRequestOptions.GetOperation(request));
    }

    private readonly record struct HttpQuotaPartition(
        SteamDependency Dependency,
        string Authority,
        string Operation);
}

internal sealed class SteamHttpQuotaHandler(
    SteamDependency dependency,
    SteamHttpRequestQuota quota,
    ISteamConnectivityMonitor connectivity,
    TimeProvider timeProvider,
    ILogger<SteamHttpQuotaHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var operation = SteamHttpRequestOptions.GetOperation(request);
        var startedAt = timeProvider.GetTimestamp();
        using var lease = await quota.AcquireAsync(dependency, request, cancellationToken).ConfigureAwait(false);
        var wait = timeProvider.GetElapsedTime(startedAt);
        if (!lease.IsAcquired)
        {
            var failure = new SteamFailure(SteamFailureKind.RateLimited, "rate_limiter_rejected");
            connectivity.ReportFailure(dependency, failure, isRateLimited: true);
            SteamTelemetry.RecordRateLimit("HTTP", dependency, operation, wait, true);
            SteamTelemetry.RecordFailure("HTTP", dependency, operation, failure.Kind);
            logger.LogWarning(
                "Steam request quota rejected {Operation} for {Dependency} after {ElapsedMs} ms",
                operation, dependency, wait.TotalMilliseconds);
            throw new SteamRateLimiterRejectedException($"Steam request quota rejected {dependency}/{operation}.");
        }

        SteamTelemetry.RecordRateLimit("HTTP", dependency, operation, wait, false);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class SteamHttpObservationHandler(
    SteamDependency dependency,
    SteamResultClassifier classifier,
    ISteamConnectivityMonitor connectivity,
    TimeProvider timeProvider,
    ILogger<SteamHttpObservationHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var operation = SteamHttpRequestOptions.GetOperation(request);
        var startedAt = timeProvider.GetTimestamp();
        using var activity = SteamTelemetry.Activities.StartActivity("steam.http.request");
        activity?.SetTag("steam.transport", "HTTP");
        activity?.SetTag("steam.dependency", dependency.ToString());
        activity?.SetTag("steam.operation", operation);
        SteamTelemetry.RecordRequest("HTTP", dependency, operation);
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var elapsed = timeProvider.GetElapsedTime(startedAt);
            if (response.IsSuccessStatusCode)
            {
                connectivity.ReportSuccess(dependency);
                logger.LogDebug(
                    "Steam HTTP operation {Operation} for {Dependency} succeeded in {ElapsedMs} ms",
                    operation, dependency, elapsed.TotalMilliseconds);
            }
            else
            {
                var failure = classifier.Classify(response.StatusCode);
                connectivity.ReportFailure(dependency, failure, isRateLimited: failure.Kind == SteamFailureKind.RateLimited);
                SteamTelemetry.RecordFailure("HTTP", dependency, operation, failure.Kind);
                logger.LogWarning(
                    "Steam HTTP operation {Operation} for {Dependency} failed in {ElapsedMs} ms with {FailureKind} ({DiagnosticCode})",
                    operation, dependency, elapsed.TotalMilliseconds, failure.Kind, failure.DiagnosticCode);
            }
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failure = classifier.Classify(exception, cancellationToken);
            var circuitOpen = failure.DiagnosticCode == "circuit_open";
            connectivity.ReportFailure(dependency, failure, circuitOpen, failure.Kind == SteamFailureKind.RateLimited);
            SteamTelemetry.RecordFailure("HTTP", dependency, operation, failure.Kind);
            logger.LogWarning(
                "Steam HTTP operation {Operation} for {Dependency} failed in {ElapsedMs} ms with {FailureKind} ({DiagnosticCode}) and {ExceptionType}",
                operation, dependency, timeProvider.GetElapsedTime(startedAt).TotalMilliseconds,
                failure.Kind, failure.DiagnosticCode, exception.GetType().Name);
            throw;
        }
    }
}
