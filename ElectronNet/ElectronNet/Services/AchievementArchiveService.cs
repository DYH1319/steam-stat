using System.Text.Json;
using ElectronNet.Helpers;
using ElectronNet.Hosting;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Events;
using SteamStat.Core.Features.Achievements;
using SteamStat.Core.Sessions;
using SteamStat.Core.Steam.Gateway;

namespace ElectronNet.Services;

/// <summary>
/// 成就档案：把每次拉取到的单游戏成就解锁明细和成就集进度永久写入 SQLite（只增不删），
/// 记录成就 schema 的变化，并提供按账号批量增量同步（只拉取解锁数与归档不一致的游戏）与目标清单。
/// </summary>
internal sealed class AchievementArchiveService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    SteamAchievementsService achievementsService,
    ISteamAccountIdentityProvider identityProvider,
    IEventBus eventBus,
    TimeProvider timeProvider,
    ILogger<AchievementArchiveService> logger) : IDisposable
{
    private const int DefaultPageSize = 500;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _syncLock = new();
    private AchievementArchiveSyncStatusDto _status = new() { State = "idle", Total = 0, Done = 0, Failed = 0, Added = 0 };
    private CancellationTokenSource? _syncCancellation;
    private Task _publishChain = Task.CompletedTask;

    /// <summary>
    /// 透传成就详情结果，顺带把已解锁成就写入档案（失败只记日志，不影响调用方）。
    /// 返回值的成就集带上档案里保留的完成状态。
    /// </summary>
    public async Task<SteamAchievementGameResult> CaptureAsync(
        string accountName,
        SteamAchievementGameResult result,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (IsObservable(result)) await ObserveSchemaAsync(result.Schema!, cancellationToken).ConfigureAwait(false);
            var outcome = await RecordAsync(accountName, result, cancellationToken).ConfigureAwait(false);
            return result with { Sets = outcome.Sets };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to archive achievements of app {AppId}", result.AppId);
        }
        return result;
    }

    /// <summary>公共数据的 schema 没有哈希，也不是最新的，比较只会产生假变化。</summary>
    private static bool IsObservable(SteamAchievementGameResult result)
        => result.Schema is { SchemaHash: not 0 } && result.SchemaState.Source != SteamDataSource.PublicData;

    private readonly record struct RecordOutcome(int Added, IReadOnlyList<SteamAchievementSet> Sets);

    private async Task<RecordOutcome> RecordAsync(
        string accountName,
        SteamAchievementGameResult result,
        CancellationToken cancellationToken,
        string? fallbackAppName = null,
        SteamAchievementAppProgressSnapshot? progress = null)
    {
        if (!result.IsSuccess || !result.ProgressState.HasValue) return new RecordOutcome(0, result.Sets);
        if (!identityProvider.TryGetSteamId(accountName, out var steamIdValue)) return new RecordOutcome(0, result.Sets);
        var steamId = steamIdValue.ToString();
        var appId = (long)result.AppId;
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var unlocked = result.Achievements
            .Where(entry => entry.IsUnlocked == true && entry.UnlockTimeUtc is { } time && time.ToUnixTimeSeconds() > 0)
            .ToArray();

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var existing = await db.AchievementUnlockArchiveTable
                .Where(row => row.AccountName == accountName && row.AppId == appId)
                .ToDictionaryAsync(row => row.InternalName, StringComparer.Ordinal, cancellationToken)
                .ConfigureAwait(false);
            var added = 0;
            foreach (var entry in unlocked)
            {
                var definition = entry.Definition;
                var unlockTime = entry.UnlockTimeUtc!.Value.ToUnixTimeSeconds();
                if (!existing.TryGetValue(definition.InternalName, out var row))
                {
                    row = new AchievementUnlockArchive
                    {
                        AccountName = accountName,
                        AppId = appId,
                        InternalName = definition.InternalName,
                        RecordedAt = now
                    };
                    db.AchievementUnlockArchiveTable.Add(row);
                    existing[definition.InternalName] = row;
                    added++;
                }
                row.SteamId = steamId;
                row.Name = Truncate(definition.LocalizedName, 512);
                row.Description = Truncate(definition.LocalizedDescription, 2048);
                row.Icon = Truncate(definition.Icon, 1024);
                row.Hidden = definition.Hidden;
                row.GlobalPercent = definition.GlobalUnlockedPercent ?? row.GlobalPercent;
                // 全球解锁率每次同步都会被覆盖，Steam 也不留历史：解锁时的稀有度只有这一次机会
                if (row.FirstPercentAt == null && definition.GlobalUnlockedPercent is { } percent)
                {
                    row.FirstGlobalPercent = percent;
                    row.FirstPercentAt = now;
                }
                row.UnlockTime = unlockTime;
                row.GroupId = definition.GroupId ?? row.GroupId;
                row.Archived = entry.IsArchived;
            }

            var game = await db.AchievementGameArchiveTable
                .FirstOrDefaultAsync(row => row.AccountName == accountName && row.AppId == appId, cancellationToken)
                .ConfigureAwait(false);
            if (game == null)
            {
                game = new AchievementGameArchive { AccountName = accountName, AppId = appId };
                db.AchievementGameArchiveTable.Add(game);
            }
            game.SteamId = steamId;
            // 名称未知时不写「App {id}」占位，保留已有名称，读取时再从 SteamApp 表兜底
            var appName = !AppNameHelper.IsPlaceholder(result.AppName) ? result.AppName
                : !AppNameHelper.IsPlaceholder(fallbackAppName) ? fallbackAppName
                : AppNameHelper.IsPlaceholder(game.AppName) ? string.Empty : game.AppName;
            game.AppName = Truncate(appName!, 512);
            game.Total = result.Summary.Total;
            game.Unlocked = result.Summary.Unlocked;
            // 公共数据的旧记录没有分组，算不出 Steam 口径：留空，等 CM 恢复后重算
            var hasSets = result.Schema!.Groups.Count > 0 || result.SchemaState.Source != SteamDataSource.PublicData;
            game.AllTotal = hasSets ? result.Summary.AllTotal : null;
            game.AllUnlocked = hasSets ? result.Summary.AllUnlocked : null;
            if (progress != null)
            {
                game.ProgressTotal = progress.Total;
                game.ProgressUnlocked = progress.Unlocked;
            }
            game.SyncedAt = now;
            var sets = await RecordSetsAsync(db, accountName, steamId, appId, result.Sets, now, cancellationToken)
                .ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new RecordOutcome(added, sets);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task<IReadOnlyList<SteamAchievementSet>> RecordSetsAsync(
        AppDbContext db,
        string accountName,
        string steamId,
        long appId,
        IReadOnlyList<SteamAchievementSet> sets,
        long now,
        CancellationToken cancellationToken)
    {
        var rows = await db.AchievementSetArchiveTable
            .Where(row => row.AccountName == accountName && row.AppId == appId)
            .ToDictionaryAsync(row => row.GroupId, cancellationToken)
            .ConfigureAwait(false);
        var output = new List<SteamAchievementSet>(sets.Count);
        foreach (var set in sets)
        {
            if (!rows.Remove(set.GroupId, out var row))
            {
                row = new AchievementSetArchive { AccountName = accountName, AppId = appId, GroupId = set.GroupId };
                db.AchievementSetArchiveTable.Add(row);
            }
            row.SteamId = steamId;
            row.Name = Truncate(set.LocalizedName, 512);
            row.DlcAppId = set.DlcAppId;
            row.Archived = set.Archived;
            row.Counted = set.Counted;
            row.Owned = set.Owned;
            row.Total = set.Total;
            row.Unlocked = set.Unlocked;
            row.AllTotal = set.AllTotal;
            row.CompletedAt ??= set.CompletedAt?.ToUnixTimeSeconds();
            row.Completed |= set.Completed;
            row.SyncedAt = now;
            output.Add(set with
            {
                Completed = row.Completed,
                CompletedAt = row.CompletedAt is { } at ? DateTimeOffset.FromUnixTimeSeconds(at) : null
            });
        }
        // 这次没有返回的集（比如对这名玩家已不可见）只是不再计入，已有的记录不删
        foreach (var row in rows.Values)
        {
            row.Counted = false;
            row.SyncedAt = now;
        }
        return output;
    }

    #region schema 变更史

    private sealed record SchemaItem(string Name, long Group, string GroupName, bool Archived);

    /// <summary>
    /// 和上一次观测比较，把新增、移除、归档、恢复、换集、改名写进变更史，并更新已归档解锁的标记。
    /// 第一次观测只建立基线。返回 schema 是否有变化。
    /// </summary>
    public async Task<bool> ObserveSchemaAsync(
        SteamAchievementSchemaSnapshot schema,
        CancellationToken cancellationToken = default)
    {
        if (schema.SchemaHash == 0 || schema.Definitions.Count == 0) return false;
        var appId = (long)schema.AppId;
        var hash = (long)schema.SchemaHash;
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var state = await db.AchievementSchemaStateTable
                .FirstOrDefaultAsync(row => row.AppId == appId, cancellationToken)
                .ConfigureAwait(false);
            if (state?.SchemaHash == hash) return false;

            var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            var items = ToSchemaItems(schema);
            var json = JsonSerializer.Serialize(items);
            if (state == null)
            {
                db.AchievementSchemaStateTable.Add(new AchievementSchemaState
                {
                    AppId = appId,
                    SchemaHash = hash,
                    Language = schema.Language,
                    Items = json,
                    ObservedAt = now
                });
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            var previous = JsonSerializer.Deserialize<Dictionary<string, SchemaItem>>(state.Items) ?? [];
            var changes = DiffSchema(previous, items, state.Language == schema.Language);
            foreach (var (internalName, name, kind, oldValue, newValue) in changes)
                db.AchievementSchemaChangeTable.Add(new AchievementSchemaChange
                {
                    AppId = appId,
                    SchemaHash = hash,
                    InternalName = internalName,
                    Name = Truncate(name, 512),
                    Kind = kind,
                    OldValue = oldValue == null ? null : Truncate(oldValue, 512),
                    NewValue = newValue == null ? null : Truncate(newValue, 512),
                    DetectedAt = now
                });
            state.SchemaHash = hash;
            state.Language = schema.Language;
            state.Items = json;
            state.ObservedAt = now;

            // 进度没变的账号不会重新同步，绝版成就的标记要在这里跟上
            if (changes.Count > 0)
            {
                var unlocks = await db.AchievementUnlockArchiveTable
                    .Where(row => row.AppId == appId)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                foreach (var unlock in unlocks)
                {
                    if (!items.TryGetValue(unlock.InternalName, out var item)) continue;
                    unlock.Archived = item.Archived;
                    unlock.GroupId = item.Group;
                }
            }
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return changes.Count > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static Dictionary<string, SchemaItem> ToSchemaItems(SteamAchievementSchemaSnapshot schema)
    {
        var groups = schema.Groups.ToDictionary(group => group.GroupId);
        var items = new Dictionary<string, SchemaItem>(StringComparer.Ordinal);
        foreach (var definition in schema.Definitions)
        {
            var groupId = definition.GroupId ?? 0;
            var group = groups.GetValueOrDefault(groupId);
            if (group is { DeveloperOnly: true } or { IsPublic: false }) continue;
            items[definition.InternalName] = new SchemaItem(
                definition.LocalizedName,
                groupId,
                group?.LocalizedName ?? string.Empty,
                definition.Archived || group?.Archived == true);
        }
        return items;
    }

    /// <param name="sameLanguage">语言不同时名称没有可比性，切换界面语言不能算改名。</param>
    private static List<(string InternalName, string Name, string Kind, string? OldValue, string? NewValue)> DiffSchema(
        IReadOnlyDictionary<string, SchemaItem> previous,
        IReadOnlyDictionary<string, SchemaItem> current,
        bool sameLanguage)
    {
        var changes = new List<(string, string, string, string?, string?)>();
        foreach (var (internalName, item) in current)
        {
            if (!previous.TryGetValue(internalName, out var before))
            {
                changes.Add((internalName, item.Name, "added", null, item.GroupName));
                continue;
            }
            if (before.Archived != item.Archived)
                changes.Add((internalName, item.Name, item.Archived ? "archived" : "restored", null, null));
            if (before.Group != item.Group)
                changes.Add((internalName, item.Name, "moved", before.GroupName, item.GroupName));
            if (sameLanguage && before.Name != item.Name)
                changes.Add((internalName, item.Name, "renamed", before.Name, item.Name));
        }
        foreach (var (internalName, before) in previous)
            if (!current.ContainsKey(internalName))
                changes.Add((internalName, before.Name, "removed", null, null));
        return changes;
    }

    public AchievementSchemaChangePageDto GetChanges(AchievementArchiveQueryRequest request)
    {
        var limit = request.Limit ?? DefaultPageSize;
        using var db = dbContextFactory.CreateDbContext();
        var apps = db.AchievementGameArchiveTable.AsNoTracking()
            .Where(row => row.AccountName == request.AccountName)
            .Select(row => row.AppId);
        var query = db.AchievementSchemaChangeTable.AsNoTracking().Where(row => apps.Contains(row.AppId));
        if (request.BeforeId.HasValue) query = query.Where(row => row.Id < request.BeforeId.Value);
        var items = query.OrderByDescending(row => row.Id).Take(limit).ToList();
        return new AchievementSchemaChangePageDto
        {
            Items = items.Select(row => new AchievementSchemaChangeDto
            {
                Id = row.Id,
                AppId = (uint)row.AppId,
                InternalName = row.InternalName,
                Name = row.Name,
                Kind = row.Kind,
                OldValue = row.OldValue,
                NewValue = row.NewValue,
                DetectedAt = row.DetectedAt
            }).ToArray(),
            NextBeforeId = items.Count == limit ? items[^1].Id : null
        };
    }

    #endregion

    public AchievementArchiveSetPageDto GetSets(AchievementArchiveQueryRequest request)
    {
        var limit = request.Limit ?? DefaultPageSize;
        using var db = dbContextFactory.CreateDbContext();
        var query = db.AchievementSetArchiveTable.AsNoTracking().Where(row => row.AccountName == request.AccountName);
        if (request.BeforeId.HasValue) query = query.Where(row => row.Id < request.BeforeId.Value);
        var items = query.OrderByDescending(row => row.Id).Take(limit).ToList();
        return new AchievementArchiveSetPageDto
        {
            Items = items.Select(row => new AchievementArchiveSetDto
            {
                Id = row.Id,
                AppId = (uint)row.AppId,
                GroupId = (uint)row.GroupId,
                Name = row.Name,
                DlcAppId = (uint?)row.DlcAppId,
                Archived = row.Archived,
                Counted = row.Counted,
                Owned = row.Owned,
                Total = row.Total,
                Unlocked = row.Unlocked,
                AllTotal = row.AllTotal,
                Completed = row.Completed,
                CompletedAt = row.CompletedAt,
                SyncedAt = row.SyncedAt
            }).ToArray(),
            NextBeforeId = items.Count == limit ? items[^1].Id : null
        };
    }

    /// <summary>成就集适配之前写的行，Total 还是旧口径，要重新计算一次。</summary>
    public IReadOnlySet<uint> GetAppsNeedingRecount(string accountName)
    {
        using var db = dbContextFactory.CreateDbContext();
        return db.AchievementGameArchiveTable.AsNoTracking()
            .Where(row => row.AccountName == accountName && row.AllTotal == null)
            .Select(row => row.AppId)
            .AsEnumerable()
            .Select(appId => (uint)appId)
            .ToHashSet();
    }

    /// <summary>按当前 schema 和解锁重新写入一款游戏。schema 变化和旧数据重算都走这里。</summary>
    public async Task RecordGameAsync(
        string accountName,
        SteamAchievementOverviewItem game,
        CancellationToken cancellationToken = default)
    {
        var name = string.IsNullOrEmpty(game.LocalizedName) ? game.Name : game.LocalizedName;
        var result = await achievementsService.GetGameAsync(accountName, game.AppId, cancellationToken).ConfigureAwait(false);
        await RecordAsync(accountName, result, cancellationToken, name, game.Progress).ConfigureAwait(false);
    }

    public AchievementArchivePageDto GetUnlocks(AchievementArchiveQueryRequest request)
    {
        var limit = request.Limit ?? DefaultPageSize;
        using var db = dbContextFactory.CreateDbContext();
        var query = db.AchievementUnlockArchiveTable.AsNoTracking().Where(row => row.AccountName == request.AccountName);
        if (request.BeforeId.HasValue) query = query.Where(row => row.Id < request.BeforeId.Value);
        var items = query.OrderByDescending(row => row.Id).Take(limit).ToList();
        return new AchievementArchivePageDto
        {
            Items = items.Select(ToDto).ToArray(),
            NextBeforeId = items.Count == limit ? items[^1].Id : null
        };
    }

    /// <summary>
    /// 最近解锁：按解锁时间倒序（档案的 Id 是写入顺序，补拉历史会让旧成就拥有更大的 Id）
    /// </summary>
    public IReadOnlyList<AchievementArchiveUnlockDto> GetRecentUnlocks(AchievementArchiveRecentRequest request)
    {
        var names = request.AccountNames.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct().ToArray();
        if (names.Length == 0) return [];
        using var db = dbContextFactory.CreateDbContext();
        var rows = db.AchievementUnlockArchiveTable.AsNoTracking()
            .Where(row => names.Contains(row.AccountName) && row.UnlockTime > 0)
            .OrderByDescending(row => row.UnlockTime)
            .ThenByDescending(row => row.Id)
            // 同一成就可能被多个账号归档：多取一些再去重
            .Take(request.Limit * names.Length)
            .ToList();
        return rows
            .DistinctBy(row => (row.AppId, row.InternalName))
            .Take(request.Limit)
            .Select(ToDto)
            .ToArray();
    }

    private static AchievementArchiveUnlockDto ToDto(AchievementUnlockArchive row) => new()
    {
        Id = row.Id,
        AppId = (uint)row.AppId,
        InternalName = row.InternalName,
        Name = row.Name,
        Description = row.Description,
        Icon = row.Icon,
        Hidden = row.Hidden,
        GlobalPercent = row.GlobalPercent,
        UnlockTime = row.UnlockTime,
        GroupId = (uint?)row.GroupId,
        Archived = row.Archived
    };

    public IReadOnlyList<AchievementArchiveGameDto> GetGames(string accountName)
    {
        using var db = dbContextFactory.CreateDbContext();
        var rows = db.AchievementGameArchiveTable.AsNoTracking()
            .Where(row => row.AccountName == accountName)
            .OrderBy(row => row.AppId)
            .ToList();
        var names = AppNameHelper.LookupNames(
            db, rows.Where(row => AppNameHelper.IsPlaceholder(row.AppName)).Select(row => row.AppId));
        return rows
            .Select(row => new AchievementArchiveGameDto
            {
                AppId = (uint)row.AppId,
                AppName = AppNameHelper.IsPlaceholder(row.AppName)
                    ? names.GetValueOrDefault(row.AppId, string.Empty)
                    : row.AppName,
                SteamId = row.SteamId,
                Total = row.Total,
                Unlocked = row.Unlocked,
                ProgressTotal = row.ProgressTotal,
                ProgressUnlocked = row.ProgressUnlocked,
                AllTotal = row.AllTotal,
                AllUnlocked = row.AllUnlocked,
                SyncedAt = row.SyncedAt
            })
            .ToArray();
    }

    /// <summary>
    /// 多账号总账（<see cref="AchievementLedger"/>）。先按 appId 分页，再只读这一页游戏的集和解锁，
    /// 解锁只取合并要用的几列（描述和图标会让一页变得很大）。
    /// </summary>
    public AchievementLedgerPageDto GetLedger(AchievementLedgerRequest request)
    {
        var names = request.AccountNames.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal).ToArray();
        if (names.Length == 0) return new AchievementLedgerPageDto { Items = [] };
        var offset = request.Offset ?? 0;
        var limit = request.Limit ?? DefaultPageSize;
        using var db = dbContextFactory.CreateDbContext();
        var gameQuery = db.AchievementGameArchiveTable.AsNoTracking().Where(row => names.Contains(row.AccountName));
        if (request.AppIds is { Count: > 0 } wanted)
        {
            var ids = wanted.Select(id => (long)id).Distinct().ToArray();
            gameQuery = gameQuery.Where(row => ids.Contains(row.AppId));
        }
        var page = gameQuery.Select(row => row.AppId).Distinct().OrderBy(appId => appId).Skip(offset).Take(limit + 1).ToList();
        var more = page.Count > limit;
        if (more) page.RemoveAt(page.Count - 1);
        if (page.Count == 0) return new AchievementLedgerPageDto { Items = [] };

        var games = gameQuery.Where(row => page.Contains(row.AppId)).ToList();
        var placeholders = AppNameHelper.LookupNames(db, games.Where(row => AppNameHelper.IsPlaceholder(row.AppName)).Select(row => row.AppId));
        var sets = db.AchievementSetArchiveTable.AsNoTracking()
            .Where(row => names.Contains(row.AccountName) && page.Contains(row.AppId))
            .ToList();
        var unlocks = db.AchievementUnlockArchiveTable.AsNoTracking()
            .Where(row => names.Contains(row.AccountName) && page.Contains(row.AppId))
            .Select(row => new { row.AccountName, row.AppId, row.InternalName, row.GroupId, row.Archived, row.UnlockTime })
            .ToList();

        var ledger = AchievementLedger.Merge(
            games.Select(row => new LedgerGameRow(
                row.AccountName,
                (uint)row.AppId,
                AppNameHelper.IsPlaceholder(row.AppName) ? placeholders.GetValueOrDefault(row.AppId, string.Empty) : row.AppName,
                row.Total,
                row.Unlocked,
                row.AllTotal,
                row.AllUnlocked)),
            sets.Select(row => new LedgerSetRow(
                row.AccountName,
                (uint)row.AppId,
                (uint)row.GroupId,
                row.Name,
                (uint?)row.DlcAppId,
                row.Archived,
                row.Counted,
                row.Owned,
                row.Total,
                row.Unlocked,
                row.AllTotal,
                row.Completed,
                row.CompletedAt,
                row.SyncedAt)),
            unlocks.Select(row => new LedgerUnlockRow(
                row.AccountName,
                (uint)row.AppId,
                row.InternalName,
                (uint?)row.GroupId,
                row.Archived,
                row.UnlockTime)));

        return new AchievementLedgerPageDto
        {
            Items = ledger.Select(game => new AchievementLedgerGameDto
            {
                AppId = game.AppId,
                AppName = game.AppName,
                Total = game.Total,
                Unlocked = game.Unlocked,
                AllTotal = game.AllTotal,
                AllUnlocked = game.AllUnlocked,
                Completed = game.Completed,
                Synthetic = game.Synthetic,
                CompletedAt = game.CompletedAt,
                Accounts = game.Accounts.Select(account => new AchievementLedgerAccountDto
                {
                    AccountName = account.AccountName,
                    Total = account.Total,
                    Unlocked = account.Unlocked,
                    Completed = account.Completed
                }).ToArray(),
                Sets = game.Sets.Select(set => new AchievementLedgerSetDto
                {
                    GroupId = set.GroupId,
                    Name = set.Name,
                    DlcAppId = set.DlcAppId,
                    Archived = set.Archived,
                    Counted = set.Counted,
                    Owned = set.Owned,
                    Total = set.Total,
                    Unlocked = set.Unlocked,
                    AllTotal = set.AllTotal,
                    Completed = set.Completed,
                    CompletedAt = set.CompletedAt,
                    Synthetic = set.Synthetic
                }).ToArray()
            }).ToArray(),
            NextOffset = more ? offset + page.Count : null
        };
    }

    #region 批量同步

    public AchievementArchiveSyncStatusDto GetSyncStatus()
    {
        lock (_syncLock) return _status;
    }

    public bool StartSync(string accountName, bool force)
    {
        CancellationToken token;
        lock (_syncLock)
        {
            if (_status.State == "running") return false;
            _syncCancellation?.Dispose();
            _syncCancellation = new CancellationTokenSource();
            token = _syncCancellation.Token;
            _status = new AchievementArchiveSyncStatusDto
            {
                State = "running",
                AccountName = accountName,
                Total = 0,
                Done = 0,
                Failed = 0,
                Added = 0,
                StartedAt = timeProvider.GetUtcNow().ToUnixTimeSeconds()
            };
        }
        Publish();
        _ = Task.Run(() => RunSyncAsync(accountName, force, token), CancellationToken.None);
        return true;
    }

    public void CancelSync()
    {
        lock (_syncLock) _syncCancellation?.Cancel();
    }

    private async Task RunSyncAsync(string accountName, bool force, CancellationToken cancellationToken)
    {
        try
        {
            var overview = await achievementsService.GetOverviewAsync(accountName, cancellationToken).ConfigureAwait(false);
            if (!overview.ProgressState.HasValue)
            {
                Finish("failed", overview.ProgressState.Failure?.ToString() ?? "overview_unavailable");
                return;
            }
            Dictionary<long, (int Total, int Unlocked, bool Recount)> archived;
            await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
            {
                // 优先用归档时记录的概览进度比较：详情统计（成就定义 + 解锁匹配）与概览口径可能永远不同
                archived = await db.AchievementGameArchiveTable.AsNoTracking()
                    .Where(row => row.AccountName == accountName)
                    .ToDictionaryAsync(
                        row => row.AppId,
                        row => (row.ProgressTotal ?? row.Total, row.ProgressUnlocked ?? row.Unlocked, row.AllTotal == null),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            // 增量：只拉取概览进度与档案不一致（或从未归档）的游戏
            var pending = overview.Games
                .Where(game => game.Progress is { Total: > 0 } progress
                    && (force
                        || !archived.TryGetValue(game.AppId, out var known)
                        || known.Recount
                        || known.Total != progress.Total
                        || known.Unlocked != progress.Unlocked))
                .OrderByDescending(game => game.LastPlayedAt)
                .ToArray();
            Update(status => status with { Total = pending.Length });

            foreach (var game in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!identityProvider.TryGetSteamId(accountName, out _))
                {
                    Finish("failed", "authenticationRequired");
                    return;
                }
                var name = string.IsNullOrEmpty(game.LocalizedName) ? game.Name : game.LocalizedName;
                Update(status => status with { CurrentAppId = game.AppId, CurrentAppName = name });
                var added = 0;
                var failed = false;
                try
                {
                    var result = force
                        ? await achievementsService.RefreshGameAsync(accountName, game.AppId, cancellationToken).ConfigureAwait(false)
                        : await achievementsService.GetGameAsync(accountName, game.AppId, cancellationToken).ConfigureAwait(false);
                    // 缓存里的解锁数落后于概览时强制刷新（详情口径可能不同，只在解锁数偏少时刷新）
                    if (!force && (!result.IsSuccess || !result.ProgressState.HasValue
                        || result.Summary.Unlocked < game.Progress!.Unlocked))
                        result = await achievementsService.RefreshGameAsync(accountName, game.AppId, cancellationToken)
                            .ConfigureAwait(false);
                    if (result.IsSuccess && result.ProgressState.HasValue)
                    {
                        if (IsObservable(result)) await ObserveSchemaAsync(result.Schema!, cancellationToken).ConfigureAwait(false);
                        added = (await RecordAsync(accountName, result, cancellationToken, name, game.Progress)
                            .ConfigureAwait(false)).Added;
                    }
                    else
                    {
                        failed = true;
                        logger.LogWarning(
                            "Achievement archive sync skipped app {AppId}: schema {SchemaCode}, progress {ProgressCode}",
                            game.AppId, result.SchemaState.DiagnosticCode, result.ProgressState.DiagnosticCode);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Failed to sync achievements of app {AppId}", game.AppId);
                    failed = true;
                }
                Update(status => status with
                {
                    Done = status.Done + 1,
                    Failed = status.Failed + (failed ? 1 : 0),
                    Added = status.Added + added
                });
            }
            Finish("completed", null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Finish("cancelled", null);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Achievement archive sync failed for {AccountName}", accountName);
            Finish("failed", exception.GetType().Name);
        }
    }

    private void Update(Func<AchievementArchiveSyncStatusDto, AchievementArchiveSyncStatusDto> change)
    {
        lock (_syncLock) _status = change(_status);
        Publish();
    }

    private void Finish(string state, string? error)
        => Update(status => status with
        {
            State = state,
            Error = error,
            CurrentAppId = null,
            CurrentAppName = null,
            FinishedAt = timeProvider.GetUtcNow().ToUnixTimeSeconds()
        });

    // 串行转发，保证渲染层按顺序收到进度（不会出现 completed 之后又收到 running）
    private void Publish()
    {
        lock (_syncLock)
        {
            var snapshot = _status;
            _publishChain = _publishChain
                .ContinueWith(_ => eventBus.PublishAsync(new AchievementArchiveSyncChanged(snapshot)), TaskScheduler.Default)
                .Unwrap();
        }
    }

    #endregion

    #region 目标清单

    public IReadOnlyList<AchievementTargetDto> GetTargets(string accountName)
    {
        using var db = dbContextFactory.CreateDbContext();
        return db.AchievementTargetTable.AsNoTracking()
            .Where(row => row.AccountName == accountName)
            .OrderByDescending(row => row.CreatedAt)
            .AsEnumerable()
            .Select(ToDto)
            .ToArray();
    }

    public async Task<AchievementTargetDto> AddTargetAsync(
        AchievementTargetAddRequest request,
        CancellationToken cancellationToken = default)
    {
        var appId = (long)request.AppId;
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.AchievementTargetTable.FirstOrDefaultAsync(
            item => item.AccountName == request.AccountName && item.AppId == appId && item.InternalName == request.InternalName,
            cancellationToken).ConfigureAwait(false);
        if (row == null)
        {
            row = new AchievementTarget
            {
                AccountName = request.AccountName,
                AppId = appId,
                InternalName = request.InternalName,
                CreatedAt = timeProvider.GetUtcNow().ToUnixTimeSeconds()
            };
            db.AchievementTargetTable.Add(row);
        }
        row.AppName = request.AppName;
        row.Name = request.Name;
        row.Description = request.Description;
        row.Icon = request.Icon;
        row.Note = string.IsNullOrWhiteSpace(request.Note) ? row.Note : request.Note;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToDto(row);
    }

    public async Task<bool> RemoveTargetAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.AchievementTargetTable.Where(row => row.Id == id)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    private static AchievementTargetDto ToDto(AchievementTarget row) => new()
    {
        Id = row.Id,
        AppId = (uint)row.AppId,
        AppName = row.AppName,
        InternalName = row.InternalName,
        Name = row.Name,
        Description = row.Description,
        Icon = row.Icon,
        Note = row.Note,
        CreatedAt = row.CreatedAt
    };

    #endregion

    private static string Truncate(string? value, int length)
        => string.IsNullOrEmpty(value) ? string.Empty : value.Length <= length ? value : value[..length];

    public void Dispose()
    {
        lock (_syncLock)
        {
            _syncCancellation?.Cancel();
            _syncCancellation?.Dispose();
            _syncCancellation = null;
        }
        _writeLock.Dispose();
    }
}
