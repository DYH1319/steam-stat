using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Events;
using SteamStat.Core.Features.Friends;

namespace ElectronNet.Services;

/// <summary>presence_group_record 的读写。</summary>
internal sealed class PresenceGroupStore(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ILogger<PresenceGroupStore> logger) : IPresenceGroupRecorder
{
    public async Task<IReadOnlyList<PresenceGroupEntry>> GetLatestAsync(string accountName, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var ids = await db.PresenceGroupTable.AsNoTracking()
            .Where(row => row.AccountName == accountName)
            .GroupBy(row => new { row.SteamId, row.Kind })
            .Select(group => group.Max(row => row.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return await db.PresenceGroupTable.AsNoTracking()
            .Where(row => ids.Contains(row.Id))
            .Select(row => new PresenceGroupEntry(row.AccountName, row.SteamId, row.IsSelf, (uint)row.AppId, row.Kind, row.GroupKey, row.ObservedAt))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordAsync(PresenceGroupEntry entry, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            db.PresenceGroupTable.Add(new PresenceGroupRecord
            {
                AccountName = entry.AccountName,
                SteamId = entry.SteamId,
                IsSelf = entry.IsSelf,
                AppId = (int)entry.AppId,
                Kind = entry.Kind,
                GroupKey = entry.GroupKey,
                ObservedAt = entry.ObservedAt
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Failed to record a presence group change");
        }
    }
}

/// <summary>
/// 记下应用能看到好友状态的时间段：CM 会话就绪时开一段，每五分钟心跳延长，会话结束时关上。
/// 应用崩溃时最后一段停在最后一次心跳，误差不超过五分钟。断线十分钟内重连接着上一段。
/// </summary>
internal sealed class FriendObservationService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    TimeProvider timeProvider,
    ILogger<FriendObservationService> logger) : IEventHandler<SteamSessionReady>, IEventHandler<SteamSessionEnded>, IAsyncDisposable
{
    internal static readonly TimeSpan Heartbeat = TimeSpan.FromMinutes(5);
    internal const long ReconnectSeconds = 10 * 60;

    private readonly Dictionary<string, long> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ITimer? _timer;
    private int _disposed;

    public async Task HandleAsync(SteamSessionReady message, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_open.ContainsKey(message.AccountName)) return;
            var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var last = await db.FriendObservationWindowTable
                .Where(row => row.AccountName == message.AccountName)
                .OrderByDescending(row => row.EndedAt)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (last != null && now - last.EndedAt <= ReconnectSeconds)
            {
                last.EndedAt = now;
            }
            else
            {
                last = new FriendObservationWindow { AccountName = message.AccountName, StartedAt = now, EndedAt = now };
                db.FriendObservationWindowTable.Add(last);
            }
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _open[message.AccountName] = last.Id;
            _timer ??= timeProvider.CreateTimer(_ => _ = BeatAsync(), null, Heartbeat, Heartbeat);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Failed to open a friend observation window");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task HandleAsync(SteamSessionEnded message, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_open.Remove(message.AccountName, out var id)) return;
            await ExtendAsync([id], cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task BeatAsync()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_open.Count > 0) await ExtendAsync(_open.Values.ToList(), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ExtendAsync(IReadOnlyList<long> ids, CancellationToken cancellationToken)
    {
        try
        {
            var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await db.FriendObservationWindowTable
                .Where(row => ids.Contains(row.Id))
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.EndedAt, now), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Failed to extend friend observation windows");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer?.Dispose();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_open.Count > 0) await ExtendAsync(_open.Values.ToList(), CancellationToken.None).ConfigureAwait(false);
            _open.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }
}
