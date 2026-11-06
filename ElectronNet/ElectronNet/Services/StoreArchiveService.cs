using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Archives.Store;
using SteamStat.Core.Environment;
using SteamStat.Core.Http;
using SteamStat.Core.Insights;
using SteamStat.Core.Settings;

namespace ElectronNet.Services;

/// <summary>
/// 数字文物档案：商店页的内容变化时留一份快照，所以下架、改名之前的样子都在。
/// 下架没有预告，只能提前存：快照随每周的商店状态检查一起做，不多发请求。
/// 封面图只存玩过的游戏，存在 {UserData}/StoreArchive/covers。
/// </summary>
internal sealed class StoreArchiveService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IHttpClientFactory httpClientFactory,
    IAppPaths appPaths,
    ISettingsStore settingsStore,
    ILogger<StoreArchiveService> logger)
{
    private const string AssetBase = "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps";
    private const long MaxCoverBytes = 4 * 1024 * 1024;

    internal string CoverDirectory => Path.Combine(appPaths.UserDataDirectory, "StoreArchive", "covers");

    /// <summary>在商店状态写入之前调用，previous 是写入之前的下架时间。</summary>
    public async Task RecordAsync(AppDbContext db, IReadOnlyList<StoreItemStatus> items, IReadOnlyDictionary<int, long?> previousDelisted, long now, CancellationToken cancellationToken = default)
    {
        var ids = items.Select(item => item.AppId).ToList();
        var latest = (await db.StorePageSnapshotTable.AsNoTracking().Where(row => ids.Contains(row.AppId)).ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(row => row.AppId)
            .ToDictionary(group => group.Key, group => group.MaxBy(row => row.CapturedAt)!);
        foreach (var item in items)
        {
            // 第一次看到一款游戏时是下架状态，那是基线，不是这时才下架的
            if (previousDelisted.TryGetValue(item.AppId, out var before))
            {
                if (item.Delisted && before == null) AddEvent(db, item.AppId, ArtifactEventKinds.Delisted, now, null, null);
                else if (!item.Delisted && before != null) AddEvent(db, item.AppId, ArtifactEventKinds.Relisted, now, null, null);
            }
            if (item.Page == null) continue;

            var known = latest.GetValueOrDefault(item.AppId);
            var fingerprint = item.Page.Fingerprint();
            if (known?.Fingerprint == fingerprint) continue;
            var previous = known == null ? null : StorePageFacts.FromJson(known.Facts);
            foreach (var change in ArtifactDiff.Compare(previous, item.Page))
                AddEvent(db, item.AppId, change.Kind, now, change.Before, change.After);
            db.StorePageSnapshotTable.Add(new StorePageSnapshot { AppId = item.AppId, CapturedAt = now, Fingerprint = fingerprint, Facts = item.Page.ToJson() });
        }
    }

    private static void AddEvent(AppDbContext db, int appId, string kind, long now, string? before, string? after)
        => db.AppArtifactEventTable.Add(new AppArtifactEvent { AppId = appId, Kind = kind, ObservedAt = now, Before = before, After = after });

    /// <summary>补齐玩过的游戏的封面。文件名是内容路径的哈希，换封面时旧图还在。</summary>
    public async Task<int> DownloadCoversAsync(CancellationToken cancellationToken = default)
    {
        if (settingsStore.GetSettings().Archive?.StoreCoversEnabled == false) return 0;
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var played = await db.UseAppRecordTable.AsNoTracking().Select(record => record.AppId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        var pending = await db.StorePageSnapshotTable.Where(row => row.CoverFile == null && played.Contains(row.AppId)).OrderByDescending(row => row.CapturedAt).Take(200).ToListAsync(cancellationToken).ConfigureAwait(false);
        var saved = 0;
        foreach (var row in pending)
        {
            var header = StorePageFacts.FromJson(row.Facts)?.HeaderAsset;
            if (string.IsNullOrEmpty(header))
            {
                row.CoverFile = string.Empty;
                continue;
            }
            var name = CoverFileName(row.AppId, header);
            var target = Path.Combine(CoverDirectory, name);
            if (!File.Exists(target) && !await DownloadAsync(row.AppId, header, target, cancellationToken).ConfigureAwait(false)) break;
            row.CoverFile = name;
            saved++;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return saved;
    }

    internal static string CoverFileName(int appId, string header)
        => $"{appId}-{Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(header)))[..16]}.jpg";

    private async Task<bool> DownloadAsync(int appId, string header, string target, CancellationToken cancellationToken)
    {
        try
        {
            using var client = httpClientFactory.CreateClient(SteamStatHttpClients.SteamCdn);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{AssetBase}/{appId}/{header}");
            SteamHttpRequestOptions.SetOperation(request, "store-cover");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxCoverBytes) return response.StatusCode == System.Net.HttpStatusCode.NotFound;
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(CoverDirectory);
            var temporary = target + ".tmp";
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, target, overwrite: true);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or SteamRateLimiterRejectedException or Polly.ExecutionRejectedException)
        {
            logger.LogInformation("Store cover download failed with {ExceptionType}", exception.GetType().Name);
            return false;
        }
    }

    internal static string SerializeIds(IReadOnlyList<int>? ids) => JsonSerializer.Serialize(ids ?? []);
}
