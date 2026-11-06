using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Archives.Store;
using SteamStat.Core.Http;
using SteamStat.Core.Insights;
using SteamStat.Core.Settings;

namespace ElectronNet.Services;

/// <summary>用户是否允许访问公开数据。</summary>
internal interface IPublicDataSwitch
{
    bool Enabled { get; }
}

/// <summary>
/// 每周查一次库里游戏的商店状态：美区价格、评测、系列、是否下架。接口是公开的，不需要 Key，
/// 只发应用 ID，不带任何账号信息。公共数据只覆盖热门游戏，小众指数和奇观榜要看的恰好是冷门游戏。
/// 用户关闭公共数据层时这里也不访问。
/// </summary>
internal sealed class StoreStatusService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IHttpClientFactory httpClientFactory,
    IInsightCatalog catalog,
    IPublicDataSwitch publicData,
    ISettingsStore settingsStore,
    StoreArchiveService archive,
    TimeProvider timeProvider,
    ILogger<StoreStatusService> logger) : IDisposable
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TickInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan RequestSpacing = TimeSpan.FromSeconds(2);
    private const long RefreshSeconds = 7 * 86400L;
    private const int BatchSize = 100;
    /// <summary>每次最多查这么多款，大库分几次查完，不一次占满网络。</summary>
    private const int MaxPerPass = 1000;

    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ITimer? _timer;

    public void StartSchedule()
        => _timer ??= timeProvider.CreateTimer(_ => _ = RunGuardedAsync(), null, InitialDelay, TickInterval);

    private async Task RunGuardedAsync()
    {
        if (!await _gate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            if (publicData.Enabled) await RefreshAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Store status refresh failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<int> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var appIds = await DueAppIdsAsync(cancellationToken).ConfigureAwait(false);
        var updated = 0;
        foreach (var batch in appIds.Chunk(BatchSize))
        {
            var items = await FetchAsync(batch, cancellationToken).ConfigureAwait(false);
            if (items == null) break;
            await SaveAsync(items, cancellationToken).ConfigureAwait(false);
            updated += items.Count;
            await Task.Delay(RequestSpacing, timeProvider, cancellationToken).ConfigureAwait(false);
        }
        if (updated > 0)
        {
            logger.LogInformation("Checked the store status of {Count} apps", updated);
            await archive.DownloadCoversAsync(cancellationToken).ConfigureAwait(false);
        }
        return updated;
    }

    private async Task<IReadOnlyList<int>> DueAppIdsAsync(CancellationToken cancellationToken)
    {
        var hidden = (settingsStore.GetSettings().Analytics?.HiddenAppIds ?? []).ToHashSet();
        var owned = (await catalog.GetOwnedGamesAsync(InsightScope.All, cancellationToken).ConfigureAwait(false)).Select(game => game.AppId);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var played = await db.UseAppRecordTable.AsNoTracking().Select(record => record.AppId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        var wished = await db.WishlistRecordTable.AsNoTracking().Where(row => row.RemovedAt == null).Select(row => row.AppId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        var cutoff = timeProvider.GetUtcNow().ToUnixTimeSeconds() - RefreshSeconds;
        // 升级前查过的游戏还没有快照，当作到期，否则要等一周才开始留存
        var snapshotted = (await db.StorePageSnapshotTable.AsNoTracking().Select(row => row.AppId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var fresh = (await db.AppStoreStatusTable.AsNoTracking().Where(status => status.CheckedAt >= cutoff).Select(status => new { status.AppId, status.DelistedAt }).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Where(status => status.DelistedAt != null || snapshotted.Contains(status.AppId))
            .Select(status => status.AppId)
            .ToHashSet();
        return owned.Concat(played).Concat(wished)
            .Where(appId => appId > 0 && !hidden.Contains(appId) && !fresh.Contains(appId))
            .Distinct()
            .Order()
            .Take(MaxPerPass)
            .ToList();
    }

    /// <summary>按某个区查一款游戏的商店内容，不写库。购前模拟用，目标游戏不在库里。</summary>
    internal async Task<StoreItemStatus?> LookupAsync(int appId, string countryCode, CancellationToken cancellationToken = default)
        => (await FetchAsync([appId], cancellationToken, countryCode).ConfigureAwait(false))?.FirstOrDefault(item => item.AppId == appId);

    private async Task<IReadOnlyList<StoreItemStatus>?> FetchAsync(IReadOnlyCollection<int> appIds, CancellationToken cancellationToken, string countryCode = StoreRegions.Default)
    {
        try
        {
            using var client = httpClientFactory.CreateClient(SteamStatHttpClients.SteamWebApi);
            var url = $"{StoreBrowseParser.Endpoint}?input_json={Uri.EscapeDataString(StoreBrowseParser.BuildRequest(appIds, countryCode))}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            SteamHttpRequestOptions.SetOperation(request, "store-status");
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            return StoreBrowseParser.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or Polly.ExecutionRejectedException or SteamRateLimiterRejectedException)
        {
            logger.LogInformation("Store status unavailable with {ExceptionType}", exception.GetType().Name);
            return null;
        }
    }

    internal async Task SaveAsync(IReadOnlyList<StoreItemStatus> items, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var ids = items.Select(item => item.AppId).ToList();
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.AppStoreStatusTable.Where(status => ids.Contains(status.AppId)).ToDictionaryAsync(status => status.AppId, cancellationToken).ConfigureAwait(false);
        await archive.RecordAsync(db, items, existing.ToDictionary(entry => entry.Key, entry => entry.Value.DelistedAt), now, cancellationToken).ConfigureAwait(false);
        foreach (var item in items)
        {
            if (!existing.TryGetValue(item.AppId, out var status))
            {
                status = new AppStoreStatus { AppId = item.AppId };
                db.AppStoreStatusTable.Add(status);
            }
            status.Visible = item.Visible;
            status.Purchasable = item.Purchasable;
            status.IsFree = item.IsFree;
            status.CheckedAt = now;
            // 下架之后商店不再给这些数字，保留下架前的
            status.PriceCents = item.PriceCents ?? status.PriceCents;
            status.ReviewCount = item.ReviewCount ?? status.ReviewCount;
            status.ReviewPercent = item.ReviewPercent ?? status.ReviewPercent;
            status.Franchise = item.Franchise ?? status.Franchise;
            status.ReleaseDate = item.ReleaseDate ?? status.ReleaseDate;
            status.DelistedAt = item.Delisted ? status.DelistedAt ?? now : null;
            if (item.TagIds is { Count: > 0 }) status.TagIds = StoreArchiveService.SerializeIds(item.TagIds);
            if (item.CategoryIds is { Count: > 0 }) status.CategoryIds = StoreArchiveService.SerializeIds(item.CategoryIds);
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
        _gate.Dispose();
    }
}
