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
    ILogger<StatsDatasetService> logger)
{
    private const int Stride = 5;

    public StatsDatasetDto Get(StatsDatasetRequest request, long lastUpdateTime)
    {
        try
        {
            var records = useAppRecordService.GetValidByParam(new UseAppRecordsQueryRequest { SteamIds = request.SteamIds });
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
            }

            var nextOffset = request.Offset + page.Count;
            var first = request.Offset == 0;
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
        return appIds.Order().Select(appId =>
        {
            var app = apps.GetValueOrDefault(appId);
            return new StatsAppDto
            {
                AppId = appId,
                Name = string.IsNullOrWhiteSpace(app?.Name) ? null : app.Name,
                Type = app?.Type,
                Developer = app?.Developer,
                Publisher = app?.Publisher,
                Installed = app?.Installed ?? false
            };
        }).ToList();
    }
}
