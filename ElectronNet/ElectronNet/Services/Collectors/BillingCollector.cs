using System.Text.Json;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using SteamStat.Core.Archives;
using SteamStat.Core.Archives.Mining;
using SteamStat.Core.Archives.Web;
using SteamStat.Core.Insights;
using SteamStat.Core.Sessions;
using SteamStat.Core.Settings;

namespace ElectronNet.Services.Collectors;

/// <summary>账单入库和商品匹配。收集和重新解析共用。</summary>
internal sealed class BillingArchive(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IInsightCatalog catalog,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>返回这页里第一次看到的行数，用来判断翻页是否可以停了。</summary>
    public async Task<int> ApplyAsync(string steamId, IReadOnlyList<BillingRow> rows, CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0) return 0;
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var keys = rows.Select(row => row.Key).ToList();
        var existing = await db.PurchaseRecordTable.Where(row => row.SteamId == steamId && keys.Contains(row.RowKey)).ToDictionaryAsync(row => row.RowKey, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var added = 0;
        foreach (var row in rows)
        {
            if (!existing.TryGetValue(row.Key, out var record))
            {
                record = new PurchaseRecord { SteamId = steamId, RowKey = row.Key, FirstSeenAt = now };
                db.PurchaseRecordTable.Add(record);
                existing[row.Key] = record;
                added++;
                record.Items = Serialize(row.Items.Select(name => new PurchaseItemMatch(name, null, MatchSources.None, 0, row.Items.Count > 1)).ToArray());
            }
            record.Date = row.Date;
            record.Kind = row.Kind;
            record.TypeText = Truncate(row.TypeText, 128);
            record.TotalCents = row.TotalCents;
            record.Currency = row.Currency;
            record.WalletChangeCents = row.WalletChangeCents;
            record.PaymentMethod = row.PaymentMethod == null ? null : Truncate(row.PaymentMethod, 128);
            // 退款后原来那一行才变成已退款，所以每次都要更新
            record.Refunded = row.Refunded;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return added;
    }

    /// <summary>
    /// 把账单里的商品名对到应用。只处理还没匹配上的行，新入库的游戏下次就能配上。
    /// 市场、钱包充值不是游戏，不匹配。
    /// </summary>
    public async Task<int> MatchAsync(string steamId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var records = await db.PurchaseRecordTable
            .Where(row => row.SteamId == steamId && (row.Kind == "purchase" || row.Kind == "gift" || row.Kind == "ingame" || row.Kind == "refund"))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (records.Count == 0) return 0;

        var owned = await catalog.GetOwnedGamesAsync(InsightScope.All, cancellationToken).ConfigureAwait(false);
        var installed = await db.SteamAppTable.AsNoTracking().Select(app => new { app.AppId, app.Name }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var candidates = owned.Select(game => new MatchCandidate(game.AppId, game.Name))
            .Concat(installed.Where(app => !string.IsNullOrWhiteSpace(app.Name)).Select(app => new MatchCandidate(app.AppId, app.Name!)))
            .ToArray();
        var (exact, editions) = PurchaseMatcher.BuildIndex(candidates);

        var accountName = await db.SteamUserTable.AsNoTracking().Where(user => user.SteamId == steamId).Select(user => user.AccountName).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var licenses = accountName == null ? [] : (await db.LibraryAcquisitionTable.AsNoTracking()
                .Where(row => row.AccountName == accountName && row.PackageId != 0 && row.AppId != 0)
                .Select(row => new { row.PackageId, row.AppId, row.AcquiredAt })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(row => row.PackageId)
            .Select(group => new LicenseOnDay(group.First().AcquiredAt / 86400 * 86400, group.Select(row => (int)row.AppId).ToArray()))
            .ToArray();

        var updated = 0;
        foreach (var record in records)
        {
            var current = Parse(record.Items);
            if (current.Count > 0 && current.All(item => item.AppId != null) && current.Sum(item => item.AllocatedCents) == record.TotalCents) continue;
            var day = record.Date / 86400 * 86400;
            var near = licenses.Where(license => Math.Abs(license.Day - day) <= 86400).ToArray();
            var matched = PurchaseMatcher.Match(current.Select(item => item.Name).ToArray(), day, record.TotalCents, exact, editions, near);
            var value = Serialize(matched);
            if (value == record.Items) continue;
            record.Items = value;
            updated++;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public static IReadOnlyList<PurchaseItemMatch> Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<PurchaseItemMatch[]>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Serialize(IReadOnlyList<PurchaseItemMatch> items) => JsonSerializer.Serialize(items, JsonOptions);

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}

/// <summary>
/// 消费档案：读 Steam 账单。第一次翻到头，之后翻到全是见过的行就停。
/// 中途失败的第一次不算完成，下次从头再来，已入库的行不会重复。
/// </summary>
internal sealed class BillingCollector(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ISteamArchiveGateway gateway,
    SteamWebFetcher fetcher,
    WebArchiveStore store,
    BillingArchive billing,
    ISteamSessionAccessor sessions,
    ISteamAccountIdentityProvider identity,
    ISettingsStore settingsStore,
    TimeProvider timeProvider) : IArchiveCollector
{
    private const int MaxPages = 300;

    internal TimeSpan Spacing { get; set; } = TimeSpan.FromSeconds(3);

    public string Id => "billing";

    public TimeSpan Interval => TimeSpan.FromDays(7);

    public async Task<CollectorOutcome> CollectAsync(CancellationToken cancellationToken)
    {
        if (settingsStore.GetSettings().Archive?.WebCaptureEnabled == false) return CollectorOutcome.Skipped;
        var accounts = ArchiveAccounts.LoggedIn(sessions, identity);
        if (accounts.Count == 0) return CollectorOutcome.Skipped;

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var complete = await CollectorData.ReadAsync<Dictionary<string, bool>>(db, Id, cancellationToken).ConfigureAwait(false);
        var total = 0;
        foreach (var account in accounts)
        {
            var session = await gateway.GetWebSessionAsync(account.AccountName, cancellationToken).ConfigureAwait(false);
            if (session == null) continue;
            var steamId = account.SteamId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var outcome = await CollectAccountAsync(session, steamId, complete.GetValueOrDefault(steamId), cancellationToken).ConfigureAwait(false);
            if (outcome.Error != null) return CollectorOutcome.Failed(outcome.Error);
            total += outcome.Added;
            complete[steamId] = true;
            await billing.MatchAsync(steamId, cancellationToken).ConfigureAwait(false);
        }
        await CollectorData.WriteAsync(db, Id, complete, cancellationToken).ConfigureAwait(false);
        return CollectorOutcome.Ok(total);
    }

    private async Task<(int Added, string? Error)> CollectAccountAsync(SteamWebSession session, string steamId, bool complete, CancellationToken cancellationToken)
    {
        var first = await fetcher.GetAsync(session, SteamWebAllowlist.BillingHistory(), cancellationToken).ConfigureAwait(false);
        if (first.Status != WebFetchStatus.Ok) return (0, ToError(first.Status));
        var page = BillingParser.ParseDocument(first.Body!);
        if (page.State == WebPageState.SignedOut) return (0, CollectorErrors.SignedOut);
        await store.SaveAsync(steamId, WebCaptureKinds.Billing, 0, first.Body!, BillingParser.ParserVersion, Label(page.State), page.Rows.Count, cancellationToken).ConfigureAwait(false);
        if (page.State == WebPageState.LayoutChanged) return (0, CollectorErrors.Layout);

        var added = await billing.ApplyAsync(steamId, page.Rows, cancellationToken).ConfigureAwait(false);
        var walletCurrency = page.Cursor == null ? null : MoneyText.CurrencyOf(page.Cursor.Currency);
        var cursor = page.Cursor;
        var number = 0;
        var newest = added;
        while (cursor != null && number < MaxPages)
        {
            // 账单从新到旧，一页里全是见过的就说明到头了（第一次除外，那次要翻完）
            if (complete && newest == 0) break;
            number++;
            await Task.Delay(Spacing, timeProvider, cancellationToken).ConfigureAwait(false);
            var more = await fetcher.PostAsync(session, SteamWebAllowlist.BillingMore(), new Dictionary<string, string>
            {
                ["cursor[wallet_txnid]"] = cursor.WalletTxnId,
                ["cursor[timestamp_newest]"] = cursor.TimestampNewest.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["cursor[balance]"] = cursor.Balance,
                ["cursor[currency]"] = cursor.Currency.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }, cancellationToken).ConfigureAwait(false);
            if (more.Status != WebFetchStatus.Ok) return (added, ToError(more.Status));
            var next = BillingParser.ParseMore(more.Body!, walletCurrency);
            await store.SaveAsync(steamId, WebCaptureKinds.Billing, number, more.Body!, BillingParser.ParserVersion, Label(next.State), next.Rows.Count, cancellationToken).ConfigureAwait(false);
            if (next.State == WebPageState.LayoutChanged) return (added, CollectorErrors.Layout);
            newest = await billing.ApplyAsync(steamId, next.Rows, cancellationToken).ConfigureAwait(false);
            added += newest;
            cursor = next.Cursor;
        }
        return (added, null);
    }

    private static string Label(WebPageState state) => state == WebPageState.Ok ? "ok" : "layout";

    internal static string ToError(WebFetchStatus status) => status switch
    {
        WebFetchStatus.SignedOut => CollectorErrors.SignedOut,
        _ => CollectorErrors.Network
    };
}
