using System.Text.Json;
using ElectronNet.Constants;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;

namespace ElectronNet.Services;

/// <summary>
/// 好友状态变化记录服务
/// 允许用户选择需要追踪的好友，当这些好友状态变化时自动记录到数据库
/// </summary>
public static class FriendStatusRecordService
{
    /// <summary>
    /// 被追踪好友集合：key = accountName, value = 被追踪好友的 SteamId 集合
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> _trackedFriends = new();

    private static readonly Lock _lockObj = new();

    /// <summary>
    /// 开始追踪指定账号下的某些好友
    /// </summary>
    public static bool StartTracking(string accountName, List<string> friendSteamIds)
    {
        if (string.IsNullOrEmpty(accountName) || friendSteamIds.Count == 0) return false;

        lock (_lockObj)
        {
            if (!_trackedFriends.TryGetValue(accountName, out var set))
            {
                set = new HashSet<string>();
                _trackedFriends[accountName] = set;
            }

            foreach (var sid in friendSteamIds)
            {
                set.Add(sid);
            }
        }

        Console.WriteLine($"{ConsoleLogPrefix.STEAM_FRIENDS} StartTracking {accountName} -> {friendSteamIds.Count} friends");
        return true;
    }

    /// <summary>
    /// 停止追踪指定账号下的某些好友
    /// </summary>
    public static bool StopTracking(string accountName, List<string> friendSteamIds)
    {
        if (string.IsNullOrEmpty(accountName) || friendSteamIds.Count == 0) return false;

        lock (_lockObj)
        {
            if (_trackedFriends.TryGetValue(accountName, out var set))
            {
                foreach (var sid in friendSteamIds)
                {
                    set.Remove(sid);
                }

                if (set.Count == 0)
                {
                    _trackedFriends.Remove(accountName);
                }
            }
        }

        Console.WriteLine($"{ConsoleLogPrefix.STEAM_FRIENDS} StopTracking {accountName} -> {friendSteamIds.Count} friends");
        return true;
    }

    /// <summary>
    /// 获取指定账号下所有被追踪的好友 SteamId 列表
    /// </summary>
    public static List<string> GetTrackedFriends(string accountName)
    {
        lock (_lockObj)
        {
            if (_trackedFriends.TryGetValue(accountName, out var set))
            {
                return set.ToList();
            }
        }

        return [];
    }

    /// <summary>
    /// 获取所有账号的追踪好友配置
    /// </summary>
    public static Dictionary<string, List<string>> GetAllTrackedFriends()
    {
        lock (_lockObj)
        {
            return _trackedFriends.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToList());
        }
    }

    /// <summary>
    /// 判断好友是否被追踪
    /// </summary>
    public static bool IsTracked(string accountName, string friendSteamId)
    {
        lock (_lockObj)
        {
            return _trackedFriends.TryGetValue(accountName, out var set) && set.Contains(friendSteamId);
        }
    }

    /// <summary>
    /// 记录一条状态变化
    /// </summary>
    public static async Task RecordChangeAsync(
        string accountName,
        string friendSteamId,
        string friendPersonaName,
        string changeType,
        object? previousValue,
        object? currentValue)
    {
        try
        {
            var record = new FriendStatusRecord
            {
                AccountName = accountName,
                FriendSteamId = friendSteamId,
                FriendPersonaName = friendPersonaName,
                ChangeType = changeType,
                PreviousValue = previousValue == null ? null : JsonSerializer.Serialize(previousValue),
                CurrentValue = currentValue == null ? null : JsonSerializer.Serialize(currentValue),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            await using var db = AppDbContext.Create();
            db.FriendStatusRecordTable.Add(record);
            await db.SaveChangesAsync();

            Console.WriteLine($"{ConsoleLogPrefix.STEAM_FRIENDS} Recorded {changeType} for {friendPersonaName} ({friendSteamId})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.STEAM_FRIENDS} Failed to record change: {ex.Message}");
        }
    }

    /// <summary>
    /// 查询记录（支持过滤）
    /// </summary>
    public static List<FriendStatusRecord> GetRecords(object? param)
    {
        try
        {
            var pd = param as Dictionary<string, object>;
            var accountName = pd?.GetValueOrDefault("accountName")?.ToString();
            var friendSteamId = pd?.GetValueOrDefault("friendSteamId")?.ToString();
            var changeType = pd?.GetValueOrDefault("changeType")?.ToString();
            long? startTime = pd?.GetValueOrDefault("startTime") is { } s ? Convert.ToInt64(s) : null;
            long? endTime = pd?.GetValueOrDefault("endTime") is { } e ? Convert.ToInt64(e) : null;
            var limit = pd?.GetValueOrDefault("limit") is { } l ? Convert.ToInt32(l) : 1000;

            using var db = AppDbContext.Create();
            var query = db.FriendStatusRecordTable.AsNoTracking();

            if (!string.IsNullOrEmpty(accountName))
            {
                query = query.Where(r => r.AccountName == accountName);
            }
            if (!string.IsNullOrEmpty(friendSteamId))
            {
                query = query.Where(r => r.FriendSteamId == friendSteamId);
            }
            if (!string.IsNullOrEmpty(changeType))
            {
                query = query.Where(r => r.ChangeType == changeType);
            }
            if (startTime.HasValue)
            {
                query = query.Where(r => r.Timestamp >= startTime.Value);
            }
            if (endTime.HasValue)
            {
                query = query.Where(r => r.Timestamp <= endTime.Value);
            }

            return query
                .OrderByDescending(r => r.Timestamp)
                .Take(limit)
                .ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} GetRecords failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// 清除记录（根据参数过滤）；如果参数全空则清空所有
    /// </summary>
    public static async Task<int> ClearRecordsAsync(object? param)
    {
        try
        {
            var pd = param as Dictionary<string, object>;
            var accountName = pd?.GetValueOrDefault("accountName")?.ToString();
            var friendSteamId = pd?.GetValueOrDefault("friendSteamId")?.ToString();

            await using var db = AppDbContext.Create();
            var query = db.FriendStatusRecordTable.AsQueryable();

            if (!string.IsNullOrEmpty(accountName))
            {
                query = query.Where(r => r.AccountName == accountName);
            }
            if (!string.IsNullOrEmpty(friendSteamId))
            {
                query = query.Where(r => r.FriendSteamId == friendSteamId);
            }

            var toRemove = query.ToList();
            db.FriendStatusRecordTable.RemoveRange(toRemove);
            await db.SaveChangesAsync();
            return toRemove.Count;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} ClearRecordsAsync failed: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// 清理指定账号的追踪配置（用户退出登录时调用）
    /// </summary>
    public static void ClearTrackingForAccount(string accountName)
    {
        lock (_lockObj)
        {
            _trackedFriends.Remove(accountName);
        }
    }
}
