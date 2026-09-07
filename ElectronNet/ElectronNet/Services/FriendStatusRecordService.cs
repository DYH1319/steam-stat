using System.Text.Json;
using System.Text.Json.Serialization;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Features;

namespace ElectronNet.Services;

/// <summary>
/// DI-managed friend tracking and status-record persistence service.
/// </summary>
public sealed class FriendStatusRecordService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    TimeProvider timeProvider,
    ILogger<FriendStatusRecordService> logger) : IFriendStatusRecorder, IDisposable
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly Dictionary<string, HashSet<string>> _trackedFriends = new();
    private readonly object _trackingLock = new();
    private int _disposed;

    public bool StartTracking(string accountName, IReadOnlyCollection<string> friendSteamIds)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (string.IsNullOrEmpty(accountName) || friendSteamIds.Count == 0) return false;

        lock (_trackingLock)
        {
            if (!_trackedFriends.TryGetValue(accountName, out var tracked))
            {
                tracked = new HashSet<string>();
                _trackedFriends[accountName] = tracked;
            }
            foreach (var steamId in friendSteamIds) tracked.Add(steamId);
        }
        logger.LogDebug("Started tracking {Count} friends for {AccountName}", friendSteamIds.Count, accountName);
        return true;
    }

    public bool StopTracking(string accountName, IReadOnlyCollection<string> friendSteamIds)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (string.IsNullOrEmpty(accountName) || friendSteamIds.Count == 0) return false;

        lock (_trackingLock)
        {
            if (_trackedFriends.TryGetValue(accountName, out var tracked))
            {
                foreach (var steamId in friendSteamIds) tracked.Remove(steamId);
                if (tracked.Count == 0) _trackedFriends.Remove(accountName);
            }
        }
        logger.LogDebug("Stopped tracking {Count} friends for {AccountName}", friendSteamIds.Count, accountName);
        return true;
    }

    public List<string> GetTrackedFriends(string accountName)
    {
        lock (_trackingLock)
            return _trackedFriends.TryGetValue(accountName, out var tracked) ? tracked.ToList() : [];
    }

    public Dictionary<string, List<string>> GetAllTrackedFriends()
    {
        lock (_trackingLock)
            return _trackedFriends.ToDictionary(pair => pair.Key, pair => pair.Value.ToList());
    }

    public bool IsTracked(string accountName, string friendSteamId)
    {
        lock (_trackingLock)
            return _trackedFriends.TryGetValue(accountName, out var tracked) && tracked.Contains(friendSteamId);
    }

    public async Task RecordChangeAsync(
        string accountName,
        string friendSteamId,
        string friendPersonaName,
        string changeType,
        FriendStatusValue? previousValue,
        FriendStatusValue? currentValue,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var record = new FriendStatusRecord
            {
                AccountName = accountName,
                FriendSteamId = friendSteamId,
                FriendPersonaName = friendPersonaName,
                ChangeType = changeType,
                PreviousValue = previousValue == null ? null : JsonSerializer.Serialize(previousValue, _jsonOptions),
                CurrentValue = currentValue == null ? null : JsonSerializer.Serialize(currentValue, _jsonOptions),
                Timestamp = timeProvider.GetUtcNow().ToUnixTimeSeconds()
            };

            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            db.FriendStatusRecordTable.Add(record);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to record {ChangeType} for friend {FriendSteamId}", changeType, friendSteamId);
        }
    }

    public List<FriendStatusRecord> GetRecords(FriendStatusRecordsQueryRequest parameter)
    {
        try
        {
            var accountName = parameter.AccountName;
            var friendSteamId = parameter.FriendSteamId;
            var changeType = parameter.ChangeType;
            var startTime = parameter.StartTime;
            var endTime = parameter.EndTime;
            var limit = parameter.Limit ?? 1000;

            using var db = dbContextFactory.CreateDbContext();
            var query = db.FriendStatusRecordTable.AsNoTracking();
            if (!string.IsNullOrEmpty(accountName)) query = query.Where(record => record.AccountName == accountName);
            if (!string.IsNullOrEmpty(friendSteamId)) query = query.Where(record => record.FriendSteamId == friendSteamId);
            if (!string.IsNullOrEmpty(changeType)) query = query.Where(record => record.ChangeType == changeType);
            if (startTime.HasValue) query = query.Where(record => record.Timestamp >= startTime.Value);
            if (endTime.HasValue) query = query.Where(record => record.Timestamp <= endTime.Value);
            return query.OrderByDescending(record => record.Timestamp).Take(limit).ToList();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to query friend status records");
            return [];
        }
    }

    public async Task<int> ClearRecordsAsync(FriendStatusRecordsClearRequest parameter, CancellationToken cancellationToken = default)
    {
        try
        {
            var accountName = parameter.AccountName;
            var friendSteamId = parameter.FriendSteamId;

            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var query = db.FriendStatusRecordTable.AsQueryable();
            if (!string.IsNullOrEmpty(accountName)) query = query.Where(record => record.AccountName == accountName);
            if (!string.IsNullOrEmpty(friendSteamId)) query = query.Where(record => record.FriendSteamId == friendSteamId);
            var records = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
            db.FriendStatusRecordTable.RemoveRange(records);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return records.Count;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to clear friend status records");
            return 0;
        }
    }

    public void ClearTrackingForAccount(string accountName)
    {
        lock (_trackingLock) _trackedFriends.Remove(accountName);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_trackingLock) _trackedFriends.Clear();
    }
}
