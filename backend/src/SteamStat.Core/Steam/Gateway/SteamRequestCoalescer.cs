using System.Collections.Concurrent;

namespace SteamStat.Core.Steam.Gateway;

public sealed class SteamRequestCoalescer<TKey> where TKey : notnull
{
    private readonly ConcurrentDictionary<(TKey Key, Type ResultType), Lazy<Task<object?>>> _inflight = new();

    internal int InflightCount => _inflight.Count;

    public async Task<TResult> RunAsync<TResult>(
        TKey key,
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken ownerToken,
        CancellationToken callerToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        callerToken.ThrowIfCancellationRequested();
        var operationKey = (key, typeof(TResult));
        var entry = _inflight.GetOrAdd(
            operationKey,
            _ => new Lazy<Task<object?>>(
                async () => await operation(ownerToken).ConfigureAwait(false),
                LazyThreadSafetyMode.ExecutionAndPublication));
        var sharedTask = entry.Value;
        _ = sharedTask.ContinueWith(
            (completed, state) =>
            {
                _ = completed.Exception;
                var (owner, completedKey, completedEntry) =
                    ((SteamRequestCoalescer<TKey>, (TKey, Type), Lazy<Task<object?>>))state!;
                ((ICollection<KeyValuePair<(TKey, Type), Lazy<Task<object?>>>>)owner._inflight)
                    .Remove(new KeyValuePair<(TKey, Type), Lazy<Task<object?>>>(completedKey, completedEntry));
            },
            (this, operationKey, entry),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        var result = await sharedTask.WaitAsync(callerToken).ConfigureAwait(false);
        return (TResult)result!;
    }
}
