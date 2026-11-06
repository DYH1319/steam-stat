using System.Text.Json;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Sessions;

namespace ElectronNet.Services.Collectors;

internal enum CollectorResult
{
    Ok,
    /// <summary>现在没法做（没有登录、开关关着），不算失败，下次调度再试。</summary>
    Skipped,
    Failed
}

internal readonly record struct CollectorOutcome(CollectorResult Result, int Items, string? Error)
{
    public static CollectorOutcome Ok(int items) => new(CollectorResult.Ok, items, null);
    public static readonly CollectorOutcome Skipped = new(CollectorResult.Skipped, 0, null);
    public static CollectorOutcome Failed(string error) => new(CollectorResult.Failed, 0, error);
}

/// <summary>失败原因。界面文案在 archives.collector.error.*。</summary>
internal static class CollectorErrors
{
    public const string SignedOut = "signedOut";
    public const string Layout = "layout";
    public const string Network = "network";
    public const string Private = "private";
    public const string Read = "read";
}

/// <summary>一项时间敏感的采集。CollectAsync 自己负责限速，调度器保证同一时间只运行一个。</summary>
internal interface IArchiveCollector
{
    string Id { get; }

    TimeSpan Interval { get; }

    Task<CollectorOutcome> CollectAsync(CancellationToken cancellationToken);
}

internal readonly record struct ArchiveAccount(string AccountName, ulong SteamId);

/// <summary>已登录的账号。CM 和网页采集都只对这些账号做。</summary>
internal static class ArchiveAccounts
{
    public static IReadOnlyList<ArchiveAccount> LoggedIn(ISteamSessionAccessor sessions, ISteamAccountIdentityProvider identity)
    {
        var accounts = new List<ArchiveAccount>();
        foreach (var name in sessions.GetLoggedInUsers())
        {
            if (identity.TryGetSteamId(name, out var steamId)) accounts.Add(new ArchiveAccount(name, steamId));
        }
        return accounts;
    }
}

/// <summary>采集器自己的小状态，存在 collector_state.data。</summary>
internal static class CollectorData
{
    public static async Task<T> ReadAsync<T>(AppDbContext db, string id, CancellationToken cancellationToken) where T : new()
    {
        var state = await db.CollectorStateTable.AsNoTracking().FirstOrDefaultAsync(row => row.Id == id, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(state?.Data)) return new T();
        try
        {
            return JsonSerializer.Deserialize<T>(state.Data) ?? new T();
        }
        catch (JsonException)
        {
            return new T();
        }
    }

    public static async Task WriteAsync<T>(AppDbContext db, string id, T value, CancellationToken cancellationToken)
    {
        var state = await db.CollectorStateTable.FirstOrDefaultAsync(row => row.Id == id, cancellationToken).ConfigureAwait(false);
        if (state == null)
        {
            state = new CollectorState { Id = id };
            db.CollectorStateTable.Add(state);
        }
        state.Data = JsonSerializer.Serialize(value);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// 档案采集的调度器。这些数据晚一天丢一天，但都不急在这一分钟：
/// 启动后稍等再开始，之后半小时看一次哪些到期了，串行运行，不和用户抢网络。
/// 运行状态写进数据库，重启不会把全部采集重跑一遍，失败也能在档案体检里看到。
/// </summary>
internal sealed class ArchiveCollectionService(
    IEnumerable<IArchiveCollector> collectors,
    WebArchiveReparser reparser,
    IDbContextFactory<AppDbContext> dbContextFactory,
    TimeProvider timeProvider,
    ILogger<ArchiveCollectionService> logger) : IDisposable
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(30);
    /// <summary>失败后不等完整周期：网络抖一下不该让周更的采集再等七天。</summary>
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(6);

    private readonly IReadOnlyList<IArchiveCollector> _collectors = collectors.ToArray();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private ITimer? _timer;

    public void StartSchedule()
        => _timer ??= timeProvider.CreateTimer(_ => _ = TickAsync(), null, InitialDelay, TickInterval);

    private async Task TickAsync()
    {
        try
        {
            await reparser.RunIfNeededAsync(_lifetime.Token).ConfigureAwait(false);
            await RunDueAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Archive collection tick failed");
        }
    }

    internal async Task RunDueAsync(CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            foreach (var collector in _collectors)
            {
                if (await IsDueAsync(collector, cancellationToken).ConfigureAwait(false))
                    await RunOneAsync(collector, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>手动触发：等当前的采集结束再跑，不看是否到期。</summary>
    public async Task<ArchiveCollectorStatusDto?> RunNowAsync(string id, CancellationToken cancellationToken = default)
    {
        var collector = _collectors.FirstOrDefault(item => item.Id == id);
        if (collector == null) return null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RunOneAsync(collector, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        return (await GetStatusAsync(cancellationToken).ConfigureAwait(false)).First(item => item.Id == id);
    }

    public async Task<IReadOnlyList<ArchiveCollectorStatusDto>> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var states = await db.CollectorStateTable.AsNoTracking().ToDictionaryAsync(row => row.Id, cancellationToken).ConfigureAwait(false);
        return _collectors.Select(collector =>
        {
            var state = states.GetValueOrDefault(collector.Id);
            return new ArchiveCollectorStatusDto
            {
                Id = collector.Id,
                IntervalHours = collector.Interval.TotalHours,
                LastRunAt = state?.LastRunAt,
                LastSuccessAt = state?.LastSuccessAt,
                LastError = state?.LastError,
                LastItems = state?.LastItems
            };
        }).ToArray();
    }

    private async Task<bool> IsDueAsync(IArchiveCollector collector, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var state = await db.CollectorStateTable.AsNoTracking().FirstOrDefaultAsync(row => row.Id == collector.Id, cancellationToken).ConfigureAwait(false);
        if (state?.LastRunAt is not { } last) return true;
        var wait = state.LastError != null && RetryAfterFailure < collector.Interval ? RetryAfterFailure : collector.Interval;
        return timeProvider.GetUtcNow().ToUnixTimeSeconds() - last >= (long)wait.TotalSeconds;
    }

    private async Task RunOneAsync(IArchiveCollector collector, CancellationToken cancellationToken)
    {
        CollectorOutcome outcome;
        try
        {
            outcome = await collector.CollectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Archive collector {Collector} failed", collector.Id);
            outcome = CollectorOutcome.Failed(CollectorErrors.Read);
        }
        if (outcome.Result == CollectorResult.Skipped) return;

        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var state = await db.CollectorStateTable.FirstOrDefaultAsync(row => row.Id == collector.Id, cancellationToken).ConfigureAwait(false);
        if (state == null)
        {
            state = new CollectorState { Id = collector.Id };
            db.CollectorStateTable.Add(state);
        }
        state.LastRunAt = now;
        state.LastError = outcome.Error;
        if (outcome.Result == CollectorResult.Ok)
        {
            state.LastSuccessAt = now;
            state.LastItems = outcome.Items;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Archive collector {Collector}: {Result}, {Items} items", collector.Id, outcome.Result, outcome.Items);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
        _gate.Dispose();
    }
}
