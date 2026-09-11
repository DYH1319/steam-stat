using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Http;

namespace SteamStat.Core.Steam.Gateway;

public interface ISteamCmOperationScheduler
{
    Task<T> RunAsync<T>(
        string accountName,
        string operation,
        long sessionGeneration,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default);
}

public sealed class SteamCmSchedulerRejectedException(string message) : Exception(message);

public sealed class SteamCmOperationScheduler : ISteamCmOperationScheduler, IAsyncDisposable
{
    private readonly SteamAccessOptions _options;
    private readonly ISteamConnectivityMonitor _connectivity;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SteamCmOperationScheduler> _logger;
    private readonly ConcurrentDictionary<CmPartitionKey, Partition> _partitions = new();
    private readonly ConcurrentDictionary<int, Task> _backgroundOperations = new();
    private readonly CancellationTokenSource _stopping = new();
    private int _nextBackgroundId;
    private int _disposed;

    public SteamCmOperationScheduler(
        SteamAccessOptions options,
        ISteamConnectivityMonitor connectivity,
        TimeProvider timeProvider,
        ILogger<SteamCmOperationScheduler> logger)
    {
        _options = options;
        _connectivity = connectivity;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<T> RunAsync<T>(
        string accountName,
        string operation,
        long sessionGeneration,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _stopping.Token);
        var partition = _partitions.GetOrAdd(
            new CmPartitionKey(accountName, operation),
            _ => new Partition(_options.CmConcurrencyLimit));
        var startedAt = _timeProvider.GetTimestamp();
        Task<T>? operationTask = null;
        var acquired = false;
        try
        {
            acquired = await partition.AcquireAsync(
                _options.CmQueueLimit, waitCancellation.Token).ConfigureAwait(false);
            var queueWait = _timeProvider.GetElapsedTime(startedAt);
            SteamTelemetry.RecordRateLimit(
                "CM", SteamDependency.CmTransport, operation, queueWait, !acquired);
            if (!acquired)
            {
                var failure = new SteamFailure(SteamFailureKind.RateLimited, "cm_scheduler_rejected");
                _connectivity.ReportFailure(SteamDependency.CmTransport, failure, isRateLimited: true);
                SteamTelemetry.RecordFailure("CM", SteamDependency.CmTransport, operation, failure.Kind);
                _logger.LogWarning(
                    "Steam CM operation {Operation} for generation {SessionGeneration} was rejected by the bounded scheduler",
                    operation, sessionGeneration);
                throw new SteamCmSchedulerRejectedException($"Steam CM scheduler rejected operation {operation}.");
            }

            using var activity = SteamTelemetry.Activities.StartActivity("steam.cm.operation");
            activity?.SetTag("steam.transport", "CM");
            activity?.SetTag("steam.dependency", SteamDependency.CmTransport.ToString());
            activity?.SetTag("steam.operation", operation);
            activity?.SetTag("steam.session_generation", sessionGeneration);
            SteamTelemetry.RecordRequest("CM", SteamDependency.CmTransport, operation);
            operationTask = action(_stopping.Token);
            TrackBackgroundOperation(operationTask);
            var result = await operationTask
                .WaitAsync(_options.CmOperationTimeout, _timeProvider, waitCancellation.Token)
                .ConfigureAwait(false);
            _connectivity.ReportSuccess(SteamDependency.CmTransport);
            _logger.LogDebug(
                "Steam CM operation {Operation} for generation {SessionGeneration} succeeded in {ElapsedMs} ms",
                operation, sessionGeneration, _timeProvider.GetElapsedTime(startedAt).TotalMilliseconds);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            throw;
        }
        catch (SteamCmSchedulerRejectedException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failure = exception is TimeoutException
                ? new SteamFailure(SteamFailureKind.Timeout, "cm_operation_timeout")
                : new SteamFailure(SteamFailureKind.Transient, "cm_operation_failed");
            _connectivity.ReportFailure(SteamDependency.CmTransport, failure);
            SteamTelemetry.RecordFailure("CM", SteamDependency.CmTransport, operation, failure.Kind);
            _logger.LogWarning(
                exception,
                "Steam CM operation {Operation} for generation {SessionGeneration} failed in {ElapsedMs} ms with {FailureKind} ({DiagnosticCode})",
                operation, sessionGeneration, _timeProvider.GetElapsedTime(startedAt).TotalMilliseconds,
                failure.Kind, failure.DiagnosticCode);
            throw;
        }
        finally
        {
            if (acquired && operationTask is { IsCompleted: false })
                TrackBackgroundOperation(ObserveAndReleaseAsync(operationTask, partition, operation, sessionGeneration));
            else if (acquired)
                partition.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopping.Cancel();
        var pending = _backgroundOperations.Values.ToArray();
        if (pending.Length > 0)
        {
            try
            {
                await Task.WhenAll(pending)
                    .WaitAsync(_options.CmShutdownTimeout, _timeProvider)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning(
                    "Timed out draining {OperationCount} Steam CM operations during scheduler shutdown",
                    pending.Length);
            }
            catch (Exception exception)
            {
                _logger.LogDebug(
                    exception,
                    "Observed completion failure while draining {OperationCount} Steam CM operations",
                    pending.Length);
            }
        }
        _partitions.Clear();
        _stopping.Dispose();
    }

    private async Task ObserveAndReleaseAsync(
        Task operationTask,
        Partition partition,
        string operation,
        long sessionGeneration)
    {
        try
        {
            await operationTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogDebug(
                exception,
                "Observed late completion failure for Steam CM operation {Operation} in generation {SessionGeneration}",
                operation, sessionGeneration);
        }
        finally
        {
            partition.Release();
        }
    }

    private void TrackBackgroundOperation(Task operation)
    {
        var id = Interlocked.Increment(ref _nextBackgroundId);
        _backgroundOperations[id] = operation;
        _ = operation.ContinueWith(
            (_, state) =>
            {
                var (operations, operationId) = ((ConcurrentDictionary<int, Task>, int))state!;
                operations.TryRemove(operationId, out Task? _);
            },
            (_backgroundOperations, id),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private readonly record struct CmPartitionKey(string AccountName, string Operation);

    private sealed class Partition(int concurrencyLimit)
    {
        private readonly SemaphoreSlim _gate = new(concurrencyLimit, concurrencyLimit);
        private int _queued;

        public async Task<bool> AcquireAsync(int queueLimit, CancellationToken cancellationToken)
        {
            if (await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return true;
            if (Interlocked.Increment(ref _queued) > queueLimit)
            {
                Interlocked.Decrement(ref _queued);
                return false;
            }
            try
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _queued);
            }
            // SemaphoreSlim removes canceled waiters lazily, so a Release can grant this
            // wait before cancellation is observed. Re-check and hand the slot back.
            if (cancellationToken.IsCancellationRequested)
            {
                _gate.Release();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return true;
        }

        public void Release() => _gate.Release();
    }
}
