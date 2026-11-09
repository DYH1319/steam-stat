using System.Globalization;
using ElectronNet.Services.Collectors;
using Microsoft.EntityFrameworkCore;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Archives;
using SteamStat.Core.Archives.Store;
using SteamStat.Core.Features;
using SteamStat.Core.Sessions;
using SteamStat.Core.Settings;

namespace ElectronNet.Services;

/// <summary>
/// 用户在哪个商店区。手动设置优先，否则依次看账号的钱包区、账单币种、界面语言。
/// 钱包区只有登录时才问得到，所以记下来，没登录时也能用，一周更新一次（换区不常见）。
/// </summary>
internal sealed class StoreRegionService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ISteamArchiveGateway gateway,
    ISteamSessionAccessor sessions,
    ISteamAccountIdentityProvider identity,
    ISettingsStore settingsStore,
    ILanguageProvider languageProvider,
    TimeProvider timeProvider)
{
    private const string StateId = "storeRegion";
    private const long RefreshSeconds = 7 * 86400L;

    private sealed record Known(string Region, long At);

    /// <param name="steamIds">账号切换器选中的账号。多个账号在不同的区时用第一个，空表示全部。</param>
    public async Task<StoreRegionDto> ResolveAsync(IReadOnlyList<string>? steamIds, CancellationToken cancellationToken = default)
    {
        var (detected, source) = await DetectAsync(steamIds, cancellationToken).ConfigureAwait(false);
        var manual = StoreRegions.Normalize(settingsStore.GetSettings().Archive?.StoreRegion);
        return new StoreRegionDto { Region = manual ?? detected, Source = manual != null ? "manual" : source, Detected = detected };
    }

    private async Task<(string Region, string Source)> DetectAsync(IReadOnlyList<string>? steamIds, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var known = await CollectorData.ReadAsync<Dictionary<string, Known>>(db, StateId, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var changed = false;
        foreach (var account in ArchiveAccounts.LoggedIn(sessions, identity))
        {
            var steamId = account.SteamId.ToString(CultureInfo.InvariantCulture);
            if (known.TryGetValue(steamId, out var entry) && now - entry.At < RefreshSeconds) continue;
            if (await gateway.GetStoreCountryAsync(account.AccountName, cancellationToken).ConfigureAwait(false) is not { } region) continue;
            known[steamId] = new Known(region, now);
            changed = true;
        }
        if (changed) await CollectorData.WriteAsync(db, StateId, known, cancellationToken).ConfigureAwait(false);

        var wanted = steamIds is { Count: > 0 } ? known.Where(entry => steamIds.Contains(entry.Key)) : known;
        var picked = wanted.OrderByDescending(entry => entry.Value.At).Select(entry => entry.Value.Region).FirstOrDefault();
        if (picked != null) return (picked, "account");

        var currencies = await db.PurchaseRecordTable.AsNoTracking()
            .Where(row => row.Currency != null && (steamIds == null || steamIds.Count == 0 || steamIds.Contains(row.SteamId)))
            .GroupBy(row => row.Currency)
            .Select(group => new { Currency = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var billed = StoreRegions.OfCurrency(currencies.OrderByDescending(item => item.Count).Select(item => item.Currency).FirstOrDefault());
        return billed != null ? (billed, "billing") : (StoreRegions.OfLanguage(languageProvider.GetSteamLanguage()), "language");
    }
}
