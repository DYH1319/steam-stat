using ElectronNet.Features.Reliability;
using ElectronNet.Services;
using Microsoft.EntityFrameworkCore;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Features.Library;
using SteamStat.Core.Insights;
using SteamStat.Core.Settings;

namespace ElectronNet.Features.Insights;

/// <summary>语义视图之外的数据：游戏库快照、公开资料和对账结果。</summary>
internal sealed class HostInsightCatalog(
    IDbContextFactory<AppDbContext> dbContextFactory,
    SteamLibraryService libraryService,
    PublicDataService publicData,
    PlaytimeHistoryService playtimeHistory,
    ISettingsStore settingsStore,
    TimeProvider timeProvider) : IInsightCatalog
{
    private static readonly char[] Separators = [';'];

    private HashSet<int> Hidden => (settingsStore.GetSettings().Analytics?.HiddenAppIds ?? []).ToHashSet();

    public async Task<IReadOnlyList<InsightOwnedGame>> GetOwnedGamesAsync(InsightScope scope, CancellationToken cancellationToken = default)
    {
        var snapshot = await libraryService.GetLibrarySnapshotAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var accounts = await db.SteamUserTable.AsNoTracking().ToDictionaryAsync(user => user.AccountName, user => user.SteamId, StringComparer.OrdinalIgnoreCase, cancellationToken).ConfigureAwait(false);
        var hidden = Hidden;
        var games = new Dictionary<int, InsightOwnedGame>();
        foreach (var (accountName, library) in snapshot.Libraries)
        {
            if (!scope.Includes(accounts.GetValueOrDefault(accountName))) continue;
            foreach (var game in library.Where(item => item.IsOwned && !hidden.Contains(item.AppId)))
            {
                var name = string.IsNullOrWhiteSpace(game.Name) ? game.NameLocalized : game.Name;
                var existing = games.GetValueOrDefault(game.AppId);
                games[game.AppId] = new InsightOwnedGame(
                    game.AppId,
                    string.IsNullOrWhiteSpace(name) ? $"App {game.AppId}" : name,
                    Math.Max(existing?.PlaytimeMinutes ?? 0, game.PlaytimeForever),
                    Math.Max(existing?.LastPlayed ?? 0, game.RtimeLastPlayed));
            }
        }
        return games.Values.ToList();
    }

    public IReadOnlyDictionary<int, InsightAppFacts> GetAppFacts(IEnumerable<int> appIds)
    {
        var ids = appIds.ToHashSet();
        using var db = dbContextFactory.CreateDbContext();
        var apps = db.SteamAppTable.AsNoTracking().Where(app => ids.Contains(app.AppId)).ToList()
            .GroupBy(app => app.AppId).ToDictionary(group => group.Key, group => group.First());
        var store = db.AppStoreStatusTable.AsNoTracking().Where(status => ids.Contains(status.AppId)).ToDictionary(status => status.AppId);
        var meta = publicData.GetAppMeta(ids);
        var popularity = publicData.GetAppPopularity(ids);
        var released = publicData.GetReleaseDates(ids);
        var facts = new Dictionary<int, InsightAppFacts>(ids.Count);
        foreach (var appId in ids)
        {
            var app = apps.GetValueOrDefault(appId);
            var shop = store.GetValueOrDefault(appId);
            var fame = popularity.GetValueOrDefault(appId);
            var record = meta.GetValueOrDefault(appId);
            facts[appId] = new InsightAppFacts(appId)
            {
                Tags = Split(record.Tags),
                Genres = Split(record.Genres),
                Developer = string.IsNullOrWhiteSpace(app?.Developer) ? record.Developers : app.Developer,
                Franchise = shop?.Franchise,
                ReleaseDate = shop?.ReleaseDate ?? (app?.SteamReleaseDate is > 0 ? app.SteamReleaseDate : released.TryGetValue(appId, out var date) ? date : null),
                PriceCents = shop?.PriceCents ?? fame?.PriceCents,
                IsFree = shop?.IsFree ?? app?.IsFreeApp,
                Delisted = shop?.DelistedAt != null,
                DelistedAt = shop?.DelistedAt,
                ReviewPercent = shop?.ReviewPercent ?? fame?.ReviewPercent,
                ReviewCount = shop?.ReviewCount ?? fame?.ReviewCount,
                Owners = fame?.Owners
            };
        }
        return facts;
    }

    public Task<InsightReconciliation?> GetReconciliationAsync(CancellationToken cancellationToken = default)
    {
        var overview = playtimeHistory.GetOverview(new PlaytimeHistoryQueryRequest());
        var hidden = Hidden;
        var games = overview.Games.Where(game => !hidden.Contains(game.AppId)).ToList();
        var boundary = settingsStore.GetSettings().Analytics?.DayBoundaryHour ?? 4;
        var gaps = ArchiveHealthRules.MergeGaps(
            overview.Discrepancies
                .Where(item => !hidden.Contains(item.AppId))
                .Select(item => new PlaytimeGap(item.From, item.To, Math.Max(0, item.OfficialSeconds - item.LocalSeconds))),
            boundary,
            timeProvider.LocalTimeZone);
        return Task.FromResult<InsightReconciliation?>(new InsightReconciliation(
            games.Count,
            games.Count(game => game.Status == "untracked"),
            games.Count(game => game.Status == "overcounted"),
            gaps.Select(gap => new InsightGap(gap.FromDay, gap.ToDay, gap.UntrackedSeconds)).ToList()));
    }

    private static IReadOnlyList<string> Split(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
