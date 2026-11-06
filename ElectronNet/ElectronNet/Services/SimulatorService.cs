using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ElectronNet.Models;
using ElectronNet.Services.Collectors;
using Microsoft.EntityFrameworkCore;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Archives;
using SteamStat.Core.Archives.Mining;
using SteamStat.Core.Archives.Store;
using SteamStat.Core.Archives.Web;
using SteamStat.Core.Features;
using SteamStat.Core.Insights;
using SteamStat.Core.Settings;

namespace ElectronNet.Services;

/// <summary>购前模拟。只在用户主动打开时运行，查询用的公开接口只带应用 ID 或搜索词。</summary>
internal sealed partial class SimulatorService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ISteamArchiveGateway gateway,
    StoreStatusService storeStatus,
    StoreRegionService regions,
    IInsightCatalog catalog,
    IPublicDataSwitch publicData,
    ILanguageProvider languageProvider,
    ISettingsStore settingsStore,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<SimulatorHitDto>> SearchAsync(SimulatorSearchRequest request, CancellationToken cancellationToken = default)
    {
        if (!publicData.Enabled) return [];
        var term = request.Term.Trim();
        if (term.Length == 0) return [];
        var owned = (await catalog.GetOwnedGamesAsync(InsightScope.All, cancellationToken).ConfigureAwait(false)).Select(game => game.AppId).ToHashSet();
        var region = (await regions.ResolveAsync(null, cancellationToken).ConfigureAwait(false)).Region;
        // 贴商店链接或应用 ID 时直接定位
        var direct = AppLink().Match(term);
        if (direct.Success && int.TryParse(direct.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var appId))
        {
            var item = await storeStatus.LookupAsync(appId, region, cancellationToken).ConfigureAwait(false);
            return item?.Page == null ? [] : [new SimulatorHitDto { AppId = appId, Name = item.Page.Name, Owned = owned.Contains(appId) }];
        }
        var hits = await gateway.SearchStoreAsync(term, languageProvider.GetSteamLanguage(), region, cancellationToken).ConfigureAwait(false);
        return hits.Select(hit => new SimulatorHitDto { AppId = hit.AppId, Name = hit.Name, Image = hit.Image, Owned = owned.Contains(hit.AppId) }).ToArray();
    }

    public async Task<SimulatorResultDto> RunAsync(SimulatorRunRequest request, CancellationToken cancellationToken = default)
    {
        var region = (await regions.ResolveAsync(request.SteamIds, cancellationToken).ConfigureAwait(false)).Region;
        if (!publicData.Enabled) return Empty(request.AppId, string.Empty, "unavailable", 0, region);
        var item = await storeStatus.LookupAsync(request.AppId, region, cancellationToken).ConfigureAwait(false);
        if (item == null) return Empty(request.AppId, string.Empty, "unavailable", 0, region);

        // 锁区的游戏在这个区看不到内容，但标签全球一样，模拟还能做，只是没有这个区的价格
        var unavailable = false;
        if (item.Page == null && region != StoreRegions.Default)
        {
            var global = await storeStatus.LookupAsync(request.AppId, StoreRegions.Default, cancellationToken).ConfigureAwait(false);
            if (global?.Page != null)
            {
                item = global with { FinalPriceCents = null, PriceCents = null, PriceText = null };
                unavailable = true;
            }
        }
        if (item.Page == null) return Empty(request.AppId, string.Empty, "notFound", 0, region);

        var library = await LoadLibraryAsync(request.SteamIds, cancellationToken).ConfigureAwait(false);
        if (item.TagIds is not { Count: > 0 }) return Empty(request.AppId, item.Page.Name, "noTags", library.Count, region);

        // 现价，不是原价：打折时用户付的是折后的钱
        long? storePrice = item.FinalPriceCents ?? item.PriceCents;
        var price = request.PriceCents ?? storePrice;
        var target = new SimulatorTarget(request.AppId, item.Page.Name, item.TagIds, item.CategoryIds ?? [], item.Page.Developers, item.Page.Franchise, price);
        var result = PurchaseSimulator.Simulate(target, library);
        return new SimulatorResultDto
        {
            AppId = request.AppId,
            Name = item.Page.Name,
            Status = "ok",
            Enough = result.Enough,
            Candidates = result.Candidates,
            TaggedGames = library.Count,
            MedianHours = result.MedianHours,
            LowHours = result.LowHours,
            HighHours = result.HighHours,
            DropProbability = result.DropProbability,
            NeverPlayedProbability = result.NeverPlayedProbability,
            PriceCents = price,
            PriceIsManual = request.PriceCents != null,
            PriceText = request.PriceCents == null ? item.PriceText : null,
            Currency = MoneyText.Parse(item.PriceText, StoreRegions.CurrencyHint(region))?.Currency,
            Region = region,
            RegionUnavailable = unavailable,
            CostPerHourCents = result.CostPerHourCents,
            Neighbors = result.Neighbors.Select(neighbor => new SimulatorNeighborDto
            {
                AppId = neighbor.AppId,
                Name = neighbor.Name,
                Similarity = neighbor.Similarity,
                Hours = Math.Round(neighbor.Hours, 1),
                Dropped = neighbor.Dropped
            }).ToArray()
        };
    }

    private static SimulatorResultDto Empty(int appId, string name, string status, int tagged, string region) => new()
    {
        AppId = appId,
        Name = name,
        Status = status,
        Enough = false,
        Candidates = 0,
        TaggedGames = tagged,
        PriceIsManual = false,
        Region = region,
        RegionUnavailable = false,
        Neighbors = []
    };

    /// <summary>有商店标签的已拥有游戏。弃坑判定和周刊讣告同一套规则，手动状态优先。</summary>
    private async Task<IReadOnlyList<SimulatorGame>> LoadLibraryAsync(IReadOnlyList<string>? steamIds, CancellationToken cancellationToken)
    {
        var scope = InsightScope.Of(steamIds);
        var owned = await catalog.GetOwnedGamesAsync(scope, cancellationToken).ConfigureAwait(false);
        var ids = owned.Select(game => game.AppId).ToList();
        var facts = catalog.GetAppFacts(ids);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var tags = await db.AppStoreStatusTable.AsNoTracking().Where(status => status.TagIds != null && ids.Contains(status.AppId)).Select(status => new { status.AppId, status.TagIds }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var tagged = tags.ToDictionary(status => status.AppId, status => Parse(status.TagIds));
        var longIds = ids.Select(id => (long)id).ToList();
        var manual = (await db.LibraryGameMetaTable.AsNoTracking().Where(row => longIds.Contains(row.AppId) && row.Status != null).Select(row => new { row.AppId, row.Status }).ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(row => row.AppId)
            .ToDictionary(group => (int)group.Key, group => group.First().Status);
        var analytics = settingsStore.GetSettings().Analytics;
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var games = new List<SimulatorGame>();
        foreach (var game in owned)
        {
            if (!tagged.TryGetValue(game.AppId, out var tagIds) || tagIds.Count == 0) continue;
            var fact = facts.GetValueOrDefault(game.AppId);
            var status = manual.GetValueOrDefault(game.AppId);
            var dropped = status != null
                ? status == "abandoned"
                : GameLifecycle.Infer(game.LastPlayed, now, analytics?.PauseDays ?? 30, analytics?.AbandonDays ?? 90) == GameLifecycle.Abandoned;
            games.Add(new SimulatorGame(game.AppId, game.Name, tagIds, fact?.Developer == null ? [] : [fact.Developer], fact?.Franchise, game.PlaytimeMinutes / 60.0, dropped));
        }
        return games;
    }

    private static IReadOnlyList<int> Parse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<int[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    [GeneratedRegex(@"^(?:https?://store\.steampowered\.com/app/)?(\d{1,10})(?:[/?#].*)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AppLink();
}
