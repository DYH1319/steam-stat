using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using ElectronNet.Helpers;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Features;
using SteamStat.Core.Features.Friends;

namespace ElectronNet.Services;

/// <summary>
/// DI-managed friend tracking and status-record persistence service.
/// </summary>
public sealed class FriendStatusRecordService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    TimeProvider timeProvider,
    ILogger<FriendStatusRecordService> logger) : IFriendStatusRecorder, IRichPresenceRecorder, IDisposable
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly Dictionary<string, HashSet<string>> _trackedFriends = new();
    private readonly object _trackingLock = new();
    private readonly SemaphoreSlim _recordGate = new(1, 1);
    private bool _trackingLoaded;
    private int _disposed;

    public async Task<bool> StartTrackingAsync(
        string accountName,
        IReadOnlyCollection<string> friendSteamIds,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (string.IsNullOrEmpty(accountName) || friendSteamIds.Count == 0) return false;

        List<string> added;
        lock (_trackingLock)
        {
            EnsureTrackingLoaded();
            if (!_trackedFriends.TryGetValue(accountName, out var tracked))
            {
                tracked = new HashSet<string>();
                _trackedFriends[accountName] = tracked;
            }
            added = friendSteamIds.Where(tracked.Add).ToList();
        }
        if (added.Count > 0)
        {
            var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            db.FriendTrackingTable.AddRange(added.Select(steamId => new FriendTracking
            {
                AccountName = accountName,
                FriendSteamId = steamId,
                CreatedAt = now
            }));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        logger.LogDebug("Started tracking {Count} friends for {AccountName}", added.Count, accountName);
        return true;
    }

    public async Task<bool> StopTrackingAsync(
        string accountName,
        IReadOnlyCollection<string> friendSteamIds,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (string.IsNullOrEmpty(accountName) || friendSteamIds.Count == 0) return false;

        lock (_trackingLock)
        {
            EnsureTrackingLoaded();
            if (_trackedFriends.TryGetValue(accountName, out var tracked))
            {
                foreach (var steamId in friendSteamIds) tracked.Remove(steamId);
                if (tracked.Count == 0) _trackedFriends.Remove(accountName);
            }
        }
        var ids = friendSteamIds.ToList();
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.FriendTrackingTable
            .Where(item => item.AccountName == accountName && ids.Contains(item.FriendSteamId))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        logger.LogDebug("Stopped tracking {Count} friends for {AccountName}", ids.Count, accountName);
        return true;
    }

    public List<string> GetTrackedFriends(string accountName)
    {
        lock (_trackingLock)
        {
            EnsureTrackingLoaded();
            return _trackedFriends.TryGetValue(accountName, out var tracked) ? tracked.ToList() : [];
        }
    }

    public bool IsTracked(string accountName, string friendSteamId)
    {
        lock (_trackingLock)
        {
            EnsureTrackingLoaded();
            return _trackedFriends.TryGetValue(accountName, out var tracked) && tracked.Contains(friendSteamId);
        }
    }

    // 首次访问时从数据库加载追踪名单（调用方持有 _trackingLock）
    private void EnsureTrackingLoaded()
    {
        if (_trackingLoaded) return;
        try
        {
            using var db = dbContextFactory.CreateDbContext();
            foreach (var item in db.FriendTrackingTable.AsNoTracking().ToList())
            {
                if (!_trackedFriends.TryGetValue(item.AccountName, out var tracked))
                {
                    tracked = new HashSet<string>();
                    _trackedFriends[item.AccountName] = tracked;
                }
                tracked.Add(item.FriendSteamId);
            }
            _trackingLoaded = true;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to load persisted friend tracking list");
        }
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
        var entered = false;
        try
        {
            // 「和上一条比较」与写入要在一起：同一位好友的两次上报可能同时到
            await _recordGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var lastJson = await db.FriendStatusRecordTable.AsNoTracking()
                .Where(item => item.AccountName == accountName && item.FriendSteamId == friendSteamId && item.ChangeType == changeType)
                .OrderByDescending(item => item.Id)
                .Select(item => item.CurrentValue)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var last = ParseValue(lastJson);
            if (last != null)
            {
                if (SameValue(changeType, last, currentValue)) return;
                previousValue = last;
            }
            else if (SameValue(changeType, previousValue, currentValue))
            {
                // 没有历史、也没有变化：只是调用方在对账
                return;
            }

            db.FriendStatusRecordTable.Add(new FriendStatusRecord
            {
                AccountName = accountName,
                FriendSteamId = friendSteamId,
                FriendPersonaName = friendPersonaName,
                ChangeType = changeType,
                PreviousValue = previousValue == null ? null : JsonSerializer.Serialize(previousValue, _jsonOptions),
                CurrentValue = currentValue == null ? null : JsonSerializer.Serialize(currentValue, _jsonOptions),
                Timestamp = timeProvider.GetUtcNow().ToUnixTimeSeconds()
            });
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
        finally
        {
            if (entered) _recordGate.Release();
        }
    }

    private FriendStatusValue? ParseValue(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<FriendStatusValue>(json, _jsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 每种变化只比它自己的那个字段：游戏名可能晚一步才解析出来，不能让它把同一款游戏判成两次变化。
    /// 和迁移 DeduplicateFriendStatusRecords 里的 SQL 保持一致。
    /// </summary>
    internal static bool SameValue(string changeType, FriendStatusValue? left, FriendStatusValue? right)
    {
        if (left == null || right == null) return left == null && right == null;
        return changeType switch
        {
            "state" => left.PersonaState == right.PersonaState,
            "game" => NormalizeGameId(left.GameId) == NormalizeGameId(right.GameId),
            "personaName" => left.PersonaName == right.PersonaName,
            SteamFriendsService.RelationshipChangeType => left.Relationship == right.Relationship,
            "richPresence" => left.RichPresence == right.RichPresence,
            _ => left == right
        };
    }

    private static string NormalizeGameId(string? gameId) => string.IsNullOrEmpty(gameId) ? "0" : gameId;

    public async Task<IReadOnlyList<RichPresenceBaseline>> GetLatestAsync(string accountName, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var ids = await db.RichPresenceRecordTable.AsNoTracking()
            .Where(record => record.AccountName == accountName)
            .GroupBy(record => record.SteamId)
            .Select(group => group.Max(record => record.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var records = await db.RichPresenceRecordTable.AsNoTracking()
            .Where(record => ids.Contains(record.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return records
            .Where(record => ulong.TryParse(record.SteamId, out _))
            .Select(record => new RichPresenceBaseline(ulong.Parse(record.SteamId), (uint)record.AppId, record.RawValues, record.DisplayText, record.Id))
            .ToList();
    }

    public async Task<long?> RecordAsync(RichPresenceRecordEntry entry, CancellationToken cancellationToken = default)
    {
        try
        {
            var record = new RichPresenceRecord
            {
                AccountName = entry.AccountName,
                SteamId = entry.SteamId,
                IsSelf = entry.IsSelf,
                AppId = (int)entry.AppId,
                RawValues = entry.RawValues,
                DisplayText = entry.DisplayText,
                Source = entry.Source,
                ObservedAt = entry.ObservedAt
            };
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            db.RichPresenceRecordTable.Add(record);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return record.Id;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Failed to record rich presence for {SteamId}", entry.SteamId);
            return null;
        }
    }

    public async Task SetDisplayTextAsync(long recordId, string text, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.RichPresenceRecordTable
            .Where(record => record.Id == recordId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(record => record.DisplayText, text), cancellationToken)
            .ConfigureAwait(false);
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
            var beforeId = parameter.BeforeId;
            var limit = parameter.Limit ?? 1000;

            using var db = dbContextFactory.CreateDbContext();
            var query = db.FriendStatusRecordTable.AsNoTracking();
            if (!string.IsNullOrEmpty(accountName)) query = query.Where(record => record.AccountName == accountName);
            if (!string.IsNullOrEmpty(friendSteamId)) query = query.Where(record => record.FriendSteamId == friendSteamId);
            if (!string.IsNullOrEmpty(changeType)) query = query.Where(record => record.ChangeType == changeType);
            if (startTime.HasValue) query = query.Where(record => record.Timestamp >= startTime.Value);
            if (endTime.HasValue) query = query.Where(record => record.Timestamp <= endTime.Value);
            if (beforeId.HasValue) query = query.Where(record => record.Id < beforeId.Value);
            // 按插入顺序（Id）倒序，便于以 beforeId 游标分页拉取完整历史
            var records = query.OrderByDescending(record => record.Id).Take(limit).ToList();
            RepairGameNames(db, records);
            return records;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to query friend status records");
            return [];
        }
    }

    /// <summary>
    /// 游戏变更记录写入时应用名可能尚未解析（存成空或「App {id}」占位），读取时用 SteamApp 表修正。
    /// </summary>
    private void RepairGameNames(AppDbContext db, List<FriendStatusRecord> records)
    {
        var pending = new List<(FriendStatusRecord Record, bool Current, JsonObject Value, long AppId)>();
        foreach (var record in records.Where(record => record.ChangeType == "game"))
        {
            Collect(record, false, record.PreviousValue);
            Collect(record, true, record.CurrentValue);
        }
        if (pending.Count == 0) return;
        Dictionary<long, string> names;
        try
        {
            names = AppNameHelper.LookupNames(db, pending.Select(item => item.AppId));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to look up app names for friend status records");
            return;
        }
        foreach (var (record, current, value, appId) in pending)
        {
            if (!names.TryGetValue(appId, out var name)) continue;
            value["gameName"] = name;
            var json = value.ToJsonString();
            if (current) record.CurrentValue = json;
            else record.PreviousValue = json;
        }

        void Collect(FriendStatusRecord record, bool current, string? json)
        {
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                if (JsonNode.Parse(json) is not JsonObject value) return;
                var gameId = value["gameId"]?.GetValue<string>();
                if (!long.TryParse(gameId, out var appId) || appId == 0) return;
                if (!AppNameHelper.IsPlaceholder(value["gameName"]?.GetValue<string>())) return;
                pending.Add((record, current, value, appId));
            }
            catch (Exception)
            {
                // 历史数据格式异常时保持原样
            }
        }
    }

    /// <summary>
    /// 推算的观测窗口里，每条记录往后算这么久：那时应用正开着，之后多久没在看不知道。
    /// </summary>
    internal const long InferredWindowSeconds = 30 * 60;

    /// <summary>
    /// 应用能看到好友状态的时间段，按开始时间升序。
    /// 观测窗口表是后来才加的：账号第一个真实窗口之前（或者根本没有窗口）的记录按记录时间推算窗口，
    /// 否则那时没有结束记录的在线区间会一直延伸到单段上限。
    /// </summary>
    public async Task<FriendObservationWindowsDto> GetObservationWindowsAsync(
        FriendObservationWindowsRequest? request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = db.FriendObservationWindowTable.AsNoTracking();
        var records = db.FriendStatusRecordTable.AsNoTracking();
        if (request?.AccountNames is { Count: > 0 } names)
        {
            query = query.Where(row => names.Contains(row.AccountName));
            records = records.Where(row => names.Contains(row.AccountName));
        }
        var rows = await query.OrderBy(row => row.StartedAt)
            .Select(row => new FriendObservationWindowDto { AccountName = row.AccountName, StartedAt = row.StartedAt, EndedAt = row.EndedAt })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var firstWindow = rows.GroupBy(row => row.AccountName).ToDictionary(group => group.Key, group => group.Min(row => row.StartedAt));
        var times = await records.Select(row => new { row.AccountName, row.Timestamp }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var inferred = times.GroupBy(row => row.AccountName).SelectMany(group => InferWindows(
            group.Key, group.Select(row => row.Timestamp), firstWindow.GetValueOrDefault(group.Key, long.MaxValue)));
        return new FriendObservationWindowsDto { Windows = [.. rows.Concat(inferred).OrderBy(row => row.StartedAt)] };
    }

    /// <summary>每条记录往后算 <see cref="InferredWindowSeconds"/>，重叠的合并；只算到 <paramref name="until"/> 之前。</summary>
    internal static IEnumerable<FriendObservationWindowDto> InferWindows(string accountName, IEnumerable<long> timestamps, long until)
    {
        long? start = null;
        var end = 0L;
        foreach (var time in timestamps.Where(time => time < until).Order())
        {
            if (start != null && time > end)
            {
                yield return new FriendObservationWindowDto { AccountName = accountName, StartedAt = start.Value, EndedAt = end };
                start = null;
            }
            start ??= time;
            end = Math.Min(Math.Max(end, time + InferredWindowSeconds), until);
        }
        if (start != null) yield return new FriendObservationWindowDto { AccountName = accountName, StartedAt = start.Value, EndedAt = end };
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_trackingLock) _trackedFriends.Clear();
    }
}
