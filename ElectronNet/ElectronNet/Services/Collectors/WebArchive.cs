using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Archives.Web;
using SteamStat.Core.Environment;

namespace ElectronNet.Services.Collectors;

/// <summary>
/// 保存原始网页。解析会随 Steam 改版失效，但页面当时的样子只有这一次机会，
/// 所以先存下来，解析失败也不丢，修好解析器后可以重新解析。
/// 写盘前先抹掉页面里的令牌（SteamWebRedactor），这些文件会进备份。
/// </summary>
internal sealed class WebArchiveStore(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IAppPaths appPaths,
    TimeProvider timeProvider)
{
    /// <summary>社区页面每周抓一次，内容都已入库，留最近几份用来重新解析。账单不删：翻页只抓一次。</summary>
    private const int KeepCommunityCaptures = 4;

    internal string Root => Path.Combine(appPaths.UserDataDirectory, "WebArchive");

    public async Task<WebCaptureRecord> SaveAsync(string steamId, string kind, int page, string content, int parserVersion, string state, int items, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var clean = SteamWebRedactor.Redact(content);
        var relative = Path.Combine(kind, steamId, $"{now:yyyyMMdd-HHmmss}-{page:D4}.gz");
        var target = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var bytes = Encoding.UTF8.GetBytes(clean);
        var temporary = target + ".tmp";
        await using (var file = File.Create(temporary))
        await using (var zip = new GZipStream(file, CompressionLevel.SmallestSize))
        {
            await zip.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        File.Move(temporary, target, overwrite: true);

        var record = new WebCaptureRecord
        {
            SteamId = steamId,
            Kind = kind,
            Page = page,
            CapturedAt = now.ToUnixTimeSeconds(),
            File = relative.Replace(Path.DirectorySeparatorChar, '/'),
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            Size = new FileInfo(target).Length,
            ParserVersion = parserVersion,
            State = state,
            Items = items
        };
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.WebCaptureTable.Add(record);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<string?> ReadAsync(WebCaptureRecord record, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(Path.Combine(Root, record.File.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path)) return null;
        await using var file = File.OpenRead(path);
        await using var zip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(zip, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PruneAsync(string steamId, string kind, CancellationToken cancellationToken = default)
    {
        if (kind == WebCaptureKinds.Billing) return;
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.WebCaptureTable.Where(row => row.SteamId == steamId && row.Kind == kind).ToListAsync(cancellationToken).ConfigureAwait(false);
        var stale = rows.GroupBy(row => row.Page)
            .SelectMany(group => group.OrderByDescending(row => row.CapturedAt).Skip(KeepCommunityCaptures))
            .ToArray();
        foreach (var row in stale)
        {
            var path = Path.Combine(Root, row.File.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path)) File.Delete(path);
        }
        db.WebCaptureTable.RemoveRange(stale);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>解析器升级后，用保存的原始页面重新解析一遍。</summary>
internal sealed class WebArchiveReparser(
    IDbContextFactory<AppDbContext> dbContextFactory,
    WebArchiveStore store,
    BillingArchive billing,
    CommunityArchive community,
    ILogger<WebArchiveReparser> logger)
{
    private static int CurrentVersion(string kind) => kind == WebCaptureKinds.Billing ? BillingParser.ParserVersion : CommunityParsers.ParserVersion;

    public async Task<int> RunIfNeededAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var stale = (await db.WebCaptureTable.OrderBy(row => row.CapturedAt).ThenBy(row => row.Page).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Where(row => row.ParserVersion < CurrentVersion(row.Kind))
            .ToList();
        var parsed = 0;
        string? walletCurrency = null;
        foreach (var row in stale)
        {
            var content = await store.ReadAsync(row, cancellationToken).ConfigureAwait(false);
            if (content == null) continue;
            try
            {
                var outcome = row.Kind == WebCaptureKinds.Billing
                    ? await ReparseBillingAsync(row, content, walletCurrency, cancellationToken).ConfigureAwait(false)
                    : await community.ReparseAsync(row, content, cancellationToken).ConfigureAwait(false);
                walletCurrency = outcome.WalletCurrency ?? walletCurrency;
                row.State = outcome.State == WebPageState.Ok ? "ok" : "layout";
                row.Items = outcome.Items;
                row.ParserVersion = CurrentVersion(row.Kind);
                parsed++;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or FormatException)
            {
                logger.LogWarning(exception, "Failed to reparse captured page {Capture}", row.Id);
            }
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (parsed > 0) logger.LogInformation("Reparsed {Count} captured pages", parsed);
        return parsed;
    }

    private async Task<(WebPageState State, int Items, string? WalletCurrency)> ReparseBillingAsync(WebCaptureRecord row, string content, string? walletCurrency, CancellationToken cancellationToken)
    {
        var page = row.Page == 0 ? BillingParser.ParseDocument(content) : BillingParser.ParseMore(content, walletCurrency);
        if (page.State == WebPageState.Ok) await billing.ApplyAsync(row.SteamId, page.Rows, cancellationToken).ConfigureAwait(false);
        var currency = page.Cursor == null ? null : MoneyText.CurrencyOf(page.Cursor.Currency);
        return (page.State, page.Rows.Count, currency);
    }
}
