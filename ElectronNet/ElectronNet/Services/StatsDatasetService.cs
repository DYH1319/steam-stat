using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;

namespace ElectronNet.Services;

/// <summary>
/// 统计页使用的紧凑数据集：已应用清洗规则的会话（分页、扁平化）+ 应用元数据 + 用户 + 历史基线。
/// 聚合计算全部在渲染层完成，便于联动 / 下钻。
/// </summary>
public sealed class StatsDatasetService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    UseAppRecordService useAppRecordService,
    PlaytimeHistoryService playtimeHistoryService,
    PublicDataService publicDataService,
    ILogger<StatsDatasetService> logger)
{
    private const int Stride = 6;

    private readonly Lock _pagingLock = new();
    private (string Key, List<UseAppRecordDto> Records)? _paging;

    /// <summary>
    /// 一次完整读取从 offset 0 开始。后续页复用第一页的结果：全表查询和清洗每次要上百毫秒，
    /// 五万条记录分七页时重复七遍；复用也保证翻页途中有新会话时各页之间不会错位。
    /// </summary>
    private List<UseAppRecordDto> LoadRecords(StatsDatasetRequest request)
    {
        var key = string.Join(',', request.SteamIds ?? []);
        lock (_pagingLock)
        {
            if (request.Offset > 0 && _paging is { } cached && cached.Key == key) return cached.Records;
        }
        var records = useAppRecordService.GetValidByParam(new UseAppRecordsQueryRequest { SteamIds = request.SteamIds });
        lock (_pagingLock) _paging = (key, records);
        return records;
    }

    /// <summary>
    /// 数据集的修订号。只看会让数据集内容变化的东西：已结束的会话（进行中会话的心跳不算）、历史基线、清洗规则。
    /// 一条聚合查询，不读记录本身。
    /// </summary>
    public StatsDatasetRevisionDto GetRevision(string cleaningRules)
    {
        using var db = dbContextFactory.CreateDbContext();
        var sessions = db.UseAppRecordTable.AsNoTracking()
            .Where(record => record.EndTime != null)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Count = group.Count(),
                MaxId = group.Max(record => record.Id),
                Duration = group.Sum(record => (long)(record.Duration ?? 0)),
                Idle = group.Sum(record => record.IdleDuration),
                Tags = group.Sum(record => (long)(record.AutoTags ?? 0)),
                Apps = group.Sum(record => (long)record.AppId),
                Starts = group.Sum(record => (long)record.StartTime),
                Updated = group.Max(record => record.UpdatedAt ?? 0)
            })
            .FirstOrDefault();
        var baselines = db.PlaytimeSnapshotTable.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(group => new { Count = group.Count(), MaxId = group.Max(snapshot => snapshot.Id) })
            .FirstOrDefault();
        var parts = string.Join('|',
            sessions?.Count ?? 0, sessions?.MaxId ?? 0, sessions?.Duration ?? 0, sessions?.Idle ?? 0, sessions?.Tags ?? 0,
            sessions?.Apps ?? 0, sessions?.Starts ?? 0, sessions?.Updated ?? 0,
            baselines?.Count ?? 0, baselines?.MaxId ?? 0, cleaningRules);
        return new StatsDatasetRevisionDto
        {
            Revision = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(parts)))[..16]
        };
    }

    public StatsDatasetDto Get(StatsDatasetRequest request, long lastUpdateTime)
    {
        try
        {
            var records = LoadRecords(request);
            var userIds = records.Select(r => r.SteamId).Distinct().Order(StringComparer.Ordinal).ToList();
            var userIndex = userIds.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);

            var page = records.Skip(request.Offset).Take(request.Limit).ToList();
            var sessions = new List<int>(page.Count * Stride);
            foreach (var record in page)
            {
                sessions.Add(record.AppId);
                sessions.Add(userIndex[record.SteamId]);
                sessions.Add(record.StartTime);
                sessions.Add(record.Duration);
                sessions.Add(Math.Clamp(record.IdleDuration, 0, record.Duration));
                sessions.Add(record.AutoTags ?? 0);
            }

            var nextOffset = request.Offset + page.Count;
            var first = request.Offset == 0;
            // 读完就释放，不让几万条记录一直留在内存里
            if (nextOffset >= records.Count)
                lock (_pagingLock) _paging = null;
            return new StatsDatasetDto
            {
                Users = first ? GetUsers(userIds) : [],
                Apps = first ? GetApps(records.Select(r => r.AppId).ToHashSet()) : [],
                Baselines = first ? playtimeHistoryService.GetBaselines(request.SteamIds) : [],
                Sessions = sessions,
                Total = records.Count,
                NextOffset = nextOffset < records.Count ? nextOffset : null,
                LastUpdateTime = lastUpdateTime
            };
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to build stats dataset");
            return new StatsDatasetDto { Users = [], Apps = [], Baselines = [], Sessions = [], Total = 0, LastUpdateTime = lastUpdateTime };
        }
    }

    private IReadOnlyList<StatsUserDto> GetUsers(IReadOnlyList<string> steamIds)
    {
        using var db = dbContextFactory.CreateDbContext();
        var users = db.SteamUserTable.AsNoTracking()
            .Where(user => steamIds.Contains(user.SteamId))
            .ToList()
            .GroupBy(user => user.SteamId)
            .ToDictionary(g => g.Key, g => g.First());
        return steamIds.Select(id => new StatsUserDto
        {
            SteamId = id,
            PersonaName = users.GetValueOrDefault(id)?.PersonaName,
            AccountName = users.GetValueOrDefault(id)?.AccountName
        }).ToList();
    }

    private IReadOnlyList<StatsAppDto> GetApps(HashSet<int> sessionAppIds)
    {
        using var db = dbContextFactory.CreateDbContext();
        var baselineAppIds = db.PlaytimeSnapshotTable.AsNoTracking().Select(s => s.AppId).Distinct().ToList();
        var appIds = sessionAppIds.Concat(baselineAppIds).ToHashSet();
        var apps = db.SteamAppTable.AsNoTracking()
            .Where(app => appIds.Contains(app.AppId))
            .ToList()
            .GroupBy(app => app.AppId)
            .ToDictionary(g => g.Key, g => g.First());
        var publicMeta = publicDataService.GetAppMeta(appIds);
        var popularity = publicDataService.GetAppPopularity(appIds);
        var store = db.AppStoreStatusTable.AsNoTracking()
            .Where(status => appIds.Contains(status.AppId))
            .ToDictionary(status => status.AppId);
        return appIds.Order().Select(appId =>
        {
            var app = apps.GetValueOrDefault(appId);
            var meta = publicMeta.GetValueOrDefault(appId);
            var shop = store.GetValueOrDefault(appId);
            var fame = popularity.GetValueOrDefault(appId);
            return new StatsAppDto
            {
                AppId = appId,
                Name = string.IsNullOrWhiteSpace(app?.Name) ? null : app.Name,
                Type = app?.Type,
                // CM 没有给出时用公共数据补充
                Developer = string.IsNullOrWhiteSpace(app?.Developer) ? meta.Developers : app.Developer,
                Publisher = string.IsNullOrWhiteSpace(app?.Publisher) ? meta.Publishers : app.Publisher,
                Installed = app?.Installed ?? false,
                Genres = meta.Genres,
                Tags = meta.Tags,
                Franchise = shop?.Franchise,
                // 商店的数字是实时的、覆盖全部游戏，公共数据只覆盖热门游戏
                ReviewPercent = shop?.ReviewPercent ?? fame?.ReviewPercent,
                ReviewCount = shop?.ReviewCount ?? fame?.ReviewCount,
                Owners = fame?.Owners
            };
        }).ToList();
    }
}
