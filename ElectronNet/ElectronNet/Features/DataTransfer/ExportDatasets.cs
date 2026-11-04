using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace ElectronNet.Features.DataTransfer;

/// <summary>时间范围（Unix 秒，闭区间），只作用于带时间维度的数据集。</summary>
internal sealed record ExportFilter(long? From, long? To)
{
    internal bool Includes(long time) => (From == null || time >= From) && (To == null || time <= To);
}

/// <summary>
/// 表格导出（CSV / xlsx）的数据集定义。列名使用稳定的英文 camelCase 键：
/// 会话表的列名与 <see cref="History.SessionCsvParser"/> 的别名兼容，导出后可以再导入。
/// 凭证、资源缓存等不属于档案的数据不在这里。
/// </summary>
internal static class ExportDatasets
{
    internal const string Sessions = "sessions";
    internal const string FriendRecords = "friendRecords";
    internal const string AchievementUnlocks = "achievementUnlocks";
    internal const string AchievementGames = "achievementGames";
    internal const string AchievementTargets = "achievementTargets";
    internal const string Library = "library";
    internal const string ProfileHistory = "profileHistory";
    internal const string PlaytimeSnapshots = "playtimeSnapshots";
    internal const string Screenshots = "screenshots";

    internal static readonly IReadOnlyList<string> All =
    [
        Sessions, FriendRecords, AchievementUnlocks, AchievementGames, AchievementTargets,
        Library, ProfileHistory, PlaytimeSnapshots, Screenshots
    ];

    private static ExportColumn Text(string key) => new(key, ExportColumnKind.Text);
    private static ExportColumn Int(string key) => new(key, ExportColumnKind.Integer);
    private static ExportColumn Real(string key) => new(key, ExportColumnKind.Real);
    private static ExportColumn Time(string key) => new(key, ExportColumnKind.Time);
    private static ExportColumn Bool(string key) => new(key, ExportColumnKind.Boolean);

    internal static readonly IReadOnlyDictionary<string, IReadOnlyList<ExportColumn>> Columns = new Dictionary<string, IReadOnlyList<ExportColumn>>
    {
        [Sessions] =
        [
            Int("id"), Int("appId"), Text("appName"), Text("steamId"), Text("accountName"),
            Time("startTime"), Time("endTime"), Int("durationSeconds"), Int("idleSeconds"), Int("foregroundSeconds"),
            Text("device"), Text("source"), Text("endReason"), Text("note"), Text("tags")
        ],
        [FriendRecords] =
        [
            Int("id"), Text("accountName"), Text("friendSteamId"), Text("friendPersonaName"), Text("changeType"),
            Text("previousValue"), Text("currentValue"), Time("timestamp")
        ],
        [AchievementUnlocks] =
        [
            Text("accountName"), Text("steamId"), Int("appId"), Text("appName"), Text("internalName"), Text("name"),
            Text("description"), Bool("hidden"), Real("globalPercent"), Time("unlockTime"), Time("recordedAt")
        ],
        [AchievementGames] =
        [
            Text("accountName"), Text("steamId"), Int("appId"), Text("appName"), Int("total"), Int("unlocked"),
            Real("completionPercent"), Int("progressTotal"), Int("progressUnlocked"), Time("syncedAt")
        ],
        [AchievementTargets] =
        [
            Text("accountName"), Int("appId"), Text("appName"), Text("internalName"), Text("name"), Text("description"),
            Text("note"), Time("createdAt")
        ],
        [Library] =
        [
            Text("accountName"), Int("appId"), Text("appName"), Time("acquiredAt"), Int("packageId"), Text("status"),
            Time("completedAt"), Int("rating"), Real("price"), Text("currency"), Text("note"), Time("updatedAt")
        ],
        [ProfileHistory] =
        [
            Text("steamId"), Text("field"), Text("value"), Text("previousValue"), Time("observedAt"), Text("source")
        ],
        [PlaytimeSnapshots] =
        [
            Text("steamId"), Text("accountName"), Int("appId"), Text("appName"), Int("playtimeMinutes"), Real("playtimeHours"),
            Text("source"), Time("firstObservedAt"), Time("lastObservedAt"), Time("lastPlayedAt")
        ],
        [Screenshots] =
        [
            Text("steamId"), Int("appId"), Text("appName"), Text("fileName"), Text("filePath"), Time("takenAt"),
            Int("fileSize"), Int("width"), Int("height"), Text("caption"), Bool("missing")
        ]
    };

    /// <summary>导出期间用到的名称查找表（应用名、账号名、设备名），一次性加载。</summary>
    internal sealed class Lookups
    {
        internal required IReadOnlyDictionary<long, string> AppNames { get; init; }
        internal required IReadOnlyDictionary<string, string> AccountNames { get; init; }
        internal required IReadOnlyDictionary<string, string> DeviceNames { get; init; }

        internal string? AppName(long appId) => AppNames.GetValueOrDefault(appId);
        internal string? AccountName(string? steamId) => steamId == null ? null : AccountNames.GetValueOrDefault(steamId);

        internal static async Task<Lookups> LoadAsync(AppDbContext db, CancellationToken cancellationToken)
        {
            var appNames = new Dictionary<long, string>();
            foreach (var game in await db.AchievementGameArchiveTable.AsNoTracking()
                         .Where(row => row.AppName != "")
                         .Select(row => new { row.AppId, row.AppName })
                         .ToListAsync(cancellationToken).ConfigureAwait(false))
            {
                appNames.TryAdd(game.AppId, game.AppName);
            }
            // steam_app 的名称更权威，覆盖成就档案里的名称
            foreach (var app in await db.SteamAppTable.AsNoTracking()
                         .Where(row => row.Name != null && row.Name != "")
                         .Select(row => new { row.AppId, row.Name })
                         .ToListAsync(cancellationToken).ConfigureAwait(false))
            {
                appNames[app.AppId] = app.Name!;
            }

            var accountNames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var user in await db.SteamUserTable.AsNoTracking()
                         .Select(row => new { row.SteamId, row.AccountName })
                         .ToListAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!string.IsNullOrEmpty(user.AccountName)) accountNames.TryAdd(user.SteamId, user.AccountName);
            }

            var deviceNames = await db.DeviceTable.AsNoTracking()
                .ToDictionaryAsync(row => row.Id, row => row.Name, cancellationToken).ConfigureAwait(false);

            return new Lookups { AppNames = appNames, AccountNames = accountNames, DeviceNames = deviceNames };
        }
    }

    internal static ExportTable Create(string id, AppDbContext db, Lookups lookups, ExportFilter filter)
        => new(id, Columns[id], id switch
        {
            Sessions => SessionRows(db, lookups, filter),
            FriendRecords => FriendRecordRows(db, filter),
            AchievementUnlocks => AchievementUnlockRows(db, lookups, filter),
            AchievementGames => AchievementGameRows(db),
            AchievementTargets => AchievementTargetRows(db),
            Library => LibraryRows(db, lookups),
            ProfileHistory => ProfileHistoryRows(db, filter),
            PlaytimeSnapshots => PlaytimeSnapshotRows(db, lookups),
            Screenshots => ScreenshotRows(db, lookups, filter),
            _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown export dataset")
        });

    private static async IAsyncEnumerable<object?[]> SessionRows(AppDbContext db, Lookups lookups, ExportFilter filter,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var query = db.UseAppRecordTable.AsNoTracking().Where(row => row.EndTime != null);
        if (filter.From != null) query = query.Where(row => row.StartTime >= filter.From);
        if (filter.To != null) query = query.Where(row => row.StartTime <= filter.To);
        await foreach (var row in query.OrderBy(row => row.StartTime).ThenBy(row => row.Id).AsAsyncEnumerable()
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return
            [
                row.Id, row.AppId, lookups.AppName(row.AppId), row.SteamId, lookups.AccountName(row.SteamId),
                row.StartTime, row.EndTime, row.Duration, row.IdleDuration, row.ForegroundDuration,
                row.DeviceId == null ? null : lookups.DeviceNames.GetValueOrDefault(row.DeviceId, row.DeviceId),
                row.Source, row.EndReason, row.Note, JoinTags(row.Tags)
            ];
        }
    }

    private static async IAsyncEnumerable<object?[]> FriendRecordRows(AppDbContext db, ExportFilter filter,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var query = db.FriendStatusRecordTable.AsNoTracking();
        if (filter.From != null) query = query.Where(row => row.Timestamp >= filter.From);
        if (filter.To != null) query = query.Where(row => row.Timestamp <= filter.To);
        await foreach (var row in query.OrderBy(row => row.Timestamp).ThenBy(row => row.Id).AsAsyncEnumerable()
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return
            [
                row.Id, row.AccountName, row.FriendSteamId, row.FriendPersonaName, row.ChangeType,
                row.PreviousValue, row.CurrentValue, row.Timestamp
            ];
        }
    }

    private static async IAsyncEnumerable<object?[]> AchievementUnlockRows(AppDbContext db, Lookups lookups, ExportFilter filter,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var query = db.AchievementUnlockArchiveTable.AsNoTracking();
        if (filter.From != null) query = query.Where(row => row.UnlockTime >= filter.From);
        if (filter.To != null) query = query.Where(row => row.UnlockTime <= filter.To);
        await foreach (var row in query.OrderBy(row => row.UnlockTime).ThenBy(row => row.Id).AsAsyncEnumerable()
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return
            [
                row.AccountName, row.SteamId, row.AppId, lookups.AppName(row.AppId), row.InternalName, row.Name,
                row.Description, row.Hidden, row.GlobalPercent, row.UnlockTime, row.RecordedAt
            ];
        }
    }

    private static async IAsyncEnumerable<object?[]> AchievementGameRows(AppDbContext db,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var row in db.AchievementGameArchiveTable.AsNoTracking()
                           .OrderBy(row => row.AccountName).ThenBy(row => row.AppId).AsAsyncEnumerable()
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var completion = row.Total > 0 ? Math.Round(row.Unlocked * 100.0 / row.Total, 2) : (double?)null;
            yield return
            [
                row.AccountName, row.SteamId, row.AppId, row.AppName, row.Total, row.Unlocked,
                completion, row.ProgressTotal, row.ProgressUnlocked, row.SyncedAt
            ];
        }
    }

    private static async IAsyncEnumerable<object?[]> AchievementTargetRows(AppDbContext db,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var row in db.AchievementTargetTable.AsNoTracking()
                           .OrderBy(row => row.CreatedAt).ThenBy(row => row.Id).AsAsyncEnumerable()
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return
            [
                row.AccountName, row.AppId, row.AppName, row.InternalName, row.Name, row.Description,
                row.Note, row.CreatedAt
            ];
        }
    }

    /// <summary>入库时间与游戏档案信息按（账号, AppID）合并成一行。</summary>
    private static async IAsyncEnumerable<object?[]> LibraryRows(AppDbContext db, Lookups lookups,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var acquisitions = await db.LibraryAcquisitionTable.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var metas = await db.LibraryGameMetaTable.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var acquisitionByKey = acquisitions
            .GroupBy(row => (row.AccountName, row.AppId))
            .ToDictionary(group => group.Key, group => group.OrderBy(row => row.AcquiredAt).First());
        var metaByKey = metas
            .GroupBy(row => (row.AccountName, row.AppId))
            .ToDictionary(group => group.Key, group => group.OrderByDescending(row => row.UpdatedAt).First());

        foreach (var key in acquisitionByKey.Keys.Union(metaByKey.Keys)
                     .OrderBy(key => key.AccountName, StringComparer.Ordinal).ThenBy(key => key.AppId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var acquisition = acquisitionByKey.GetValueOrDefault(key);
            var meta = metaByKey.GetValueOrDefault(key);
            yield return
            [
                key.AccountName, key.AppId, lookups.AppName(key.AppId), acquisition?.AcquiredAt, acquisition?.PackageId,
                meta?.Status, meta?.CompletedAt, meta?.Rating, meta?.Price, meta?.Currency, meta?.Note, meta?.UpdatedAt
            ];
        }
    }

    private static async IAsyncEnumerable<object?[]> ProfileHistoryRows(AppDbContext db, ExportFilter filter,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var query = db.ProfileHistoryTable.AsNoTracking();
        if (filter.From != null) query = query.Where(row => row.ObservedAt >= filter.From);
        if (filter.To != null) query = query.Where(row => row.ObservedAt <= filter.To);
        await foreach (var row in query.OrderBy(row => row.ObservedAt).ThenBy(row => row.Id).AsAsyncEnumerable()
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return [row.SteamId, row.Field, row.Value, row.PreviousValue, row.ObservedAt, row.Source];
        }
    }

    private static async IAsyncEnumerable<object?[]> PlaytimeSnapshotRows(AppDbContext db, Lookups lookups,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var row in db.PlaytimeSnapshotTable.AsNoTracking()
                           .OrderBy(row => row.SteamId).ThenBy(row => row.AppId).ThenBy(row => row.FirstObservedAt)
                           .AsAsyncEnumerable().WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return
            [
                row.SteamId, lookups.AccountName(row.SteamId), row.AppId, lookups.AppName(row.AppId), row.PlaytimeMinutes,
                Math.Round(row.PlaytimeMinutes / 60.0, 2), row.Source, row.FirstObservedAt, row.LastObservedAt, row.LastPlayedAt
            ];
        }
    }

    private static async IAsyncEnumerable<object?[]> ScreenshotRows(AppDbContext db, Lookups lookups, ExportFilter filter,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var query = db.ScreenshotArchiveTable.AsNoTracking();
        if (filter.From != null) query = query.Where(row => row.TakenAt >= filter.From);
        if (filter.To != null) query = query.Where(row => row.TakenAt <= filter.To);
        await foreach (var row in query.OrderBy(row => row.TakenAt).ThenBy(row => row.Id).AsAsyncEnumerable()
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return
            [
                row.SteamId, row.AppId, lookups.AppName(row.AppId), row.FileName, row.FilePath, row.TakenAt,
                row.FileSize, row.Width, row.Height, row.Caption, row.MissingSince != null
            ];
        }
    }

    /// <summary>会话标签在库里是 JSON 数组；导出为「; 」分隔（导入器同样按 ; | , 拆分）。</summary>
    internal static string? JoinTags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags)) return null;
        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(tags);
            return list is { Count: > 0 } ? string.Join("; ", list) : null;
        }
        catch (JsonException)
        {
            return tags;
        }
    }
}
