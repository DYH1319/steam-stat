using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Archives.Store;
using SteamStat.Core.Features.Apps.Contracts;
using SteamStat.Core.Steam.Gateway;

namespace ElectronNet.Services;

/// <summary>
/// 游戏素材地址。主要来源是 CM 的应用目录；没登录、或目录里没有这款游戏（愿望单里的新游戏）时，
/// 用商店快照里存下的 header / main_capsule：新游戏的素材在带哈希的 store_item_assets 路径下，旧路径里没有。
/// </summary>
public sealed class SteamAppImagesService(ISteamAppCatalogGateway catalog, IDbContextFactory<AppDbContext>? dbContextFactory = null)
{
    private readonly SemaphoreSlim _concurrency = new(4);

    public async Task<IReadOnlyList<SteamAppImagesDto>> GetAsync(SteamAppImagesRequest request)
    {
        if (request.AppIds.Count > 20 || request.AppIds.Any(id => id == 0))
            throw new ArgumentException("Expected at most 20 valid Steam app ids.");
        var language = request.Language ?? "english";
        var results = await Task.WhenAll(request.AppIds.Distinct().Select(async id =>
        {
            await _concurrency.WaitAsync().ConfigureAwait(false);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var result = await catalog.GetAppAsync((uint)id, language, cancellationToken: timeout.Token)
                    .ConfigureAwait(false);
                // Old name-only cache payloads are valid, but cannot satisfy an artwork request.
                if (Incomplete(result.Value?.Artwork) && result.Source == SteamDataSource.Sqlite)
                    result = await catalog.GetAppAsync((uint)id, language,
                        refreshMode: SteamRefreshMode.RequireRefresh, cancellationToken: timeout.Token)
                        .ConfigureAwait(false);
                var images = result.Value?.Artwork;
                return new SteamAppImagesDto(id, images?.Capsule ?? [], images?.Header ?? [],
                    images?.Cover ?? [], images?.Hero ?? [], Incomplete(images));
            }
            catch (OperationCanceledException)
            {
                return new SteamAppImagesDto(id, [], [], [], [], true);
            }
            finally
            {
                _concurrency.Release();
            }
        })).ConfigureAwait(false);
        return await WithStoreAssetsAsync(results).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<SteamAppImagesDto>> WithStoreAssetsAsync(SteamAppImagesDto[] results)
    {
        var missing = results.Where(image => image.Capsule.Count == 0 && image.Header.Count == 0).Select(image => (int)image.AppId).ToList();
        if (dbContextFactory == null || missing.Count == 0) return results;
        Dictionary<int, StorePageFacts> facts;
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
            var rows = await db.StorePageSnapshotTable.AsNoTracking()
                .Where(row => missing.Contains(row.AppId))
                .OrderByDescending(row => row.CapturedAt)
                .Select(row => new { row.AppId, row.Facts })
                .ToListAsync().ConfigureAwait(false);
            facts = rows
                .GroupBy(row => row.AppId)
                .Select(group => (group.Key, Facts: StorePageFacts.FromJson(group.First().Facts)))
                .Where(entry => entry.Facts != null)
                .ToDictionary(entry => entry.Key, entry => entry.Facts!);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 快照只是补充，读不到就按 CM 的结果返回
            return results;
        }
        return results.Select(image => facts.TryGetValue((int)image.AppId, out var page)
            ? image with
            {
                Capsule = AssetUrls(image.AppId, page.CapsuleAsset, page.HeaderAsset),
                Header = AssetUrls(image.AppId, page.HeaderAsset, page.CapsuleAsset)
            }
            : image).ToArray();
    }

    private static IReadOnlyList<string> AssetUrls(long appId, params string?[] files)
        => files
            .Where(file => !string.IsNullOrWhiteSpace(file))
            .Select(file => string.Create(CultureInfo.InvariantCulture, $"{ArchiveQueryService.AssetBase}/{appId}/{file}"))
            .Distinct()
            .ToArray();

    private static bool Incomplete(SteamAppArtwork? images)
        => images == null || images.Cover.Count == 0 || images.Hero.Count == 0;
}
