using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace SteamStat.Core.Archives.Web;

public static class BillingKinds
{
    public const string Purchase = "purchase";
    public const string Gift = "gift";
    public const string InGame = "ingame";
    public const string Market = "market";
    public const string Wallet = "wallet";
    public const string Refund = "refund";
    public const string Other = "other";
}

/// <summary>账单上的一行。Date 是当天 0 点（UTC）：页面只给到日。</summary>
public sealed record BillingRow(
    string Key,
    string? TransactionId,
    long Date,
    string Kind,
    string TypeText,
    IReadOnlyList<string> Items,
    long TotalCents,
    string? Currency,
    string TotalText,
    long? WalletChangeCents,
    long? WalletBalanceCents,
    string? PaymentMethod,
    bool Refunded);

/// <summary>账单翻页的游标，原样传回去。</summary>
public sealed record BillingCursor(string WalletTxnId, long TimestampNewest, string Balance, int Currency);

public enum WebPageState
{
    Ok,
    /// <summary>页面是登录页。</summary>
    SignedOut,
    /// <summary>页面结构和解析器预期的不一样，Steam 可能改版了。原始页面还在，修好解析器后可以重新解析。</summary>
    LayoutChanged
}

public sealed record BillingPage(WebPageState State, IReadOnlyList<BillingRow> Rows, BillingCursor? Cursor, int SkippedRows);

/// <summary>
/// 解析 store.steampowered.com/account/history 和它的翻页接口。请求固定用英文，
/// 类型文字才能当成判断依据；金额的币种由钱包币种兜底。
/// 改动判断逻辑时要加 ParserVersion，已保存的原始页面会重新解析一遍。
/// </summary>
public static partial class BillingParser
{
    public const int ParserVersion = 1;

    private static readonly string[] DateFormats = ["d MMM, yyyy", "MMM d, yyyy", "d MMM yyyy", "MMM d yyyy", "d MMMM, yyyy", "MMMM d, yyyy"];

    /// <summary>账单首页。</summary>
    public static BillingPage ParseDocument(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var document = new HtmlParser().ParseDocument(html);
        if (IsSignIn(document)) return new BillingPage(WebPageState.SignedOut, [], null, 0);

        var cursor = ReadCursor(html);
        var walletCurrency = cursor == null ? null : MoneyText.CurrencyOf(cursor.Currency);
        var table = document.QuerySelector("table.wallet_history_table") ?? document.QuerySelector(".wallet_history_table");
        var rows = document.QuerySelectorAll(".wallet_table_row");
        if (rows.Length > 0) return ParseRows(rows, cursor, walletCurrency);
        // 新账号的账单是空的，表格还在；表格都没有才是改版
        return table != null || document.QuerySelector(".no_transactions, #main_content .wallet_history_click_hint") != null
            ? new BillingPage(WebPageState.Ok, [], cursor, 0)
            : new BillingPage(WebPageState.LayoutChanged, [], null, 0);
    }

    /// <summary>AjaxLoadMoreHistory 的响应：{ html, cursor }。</summary>
    public static BillingPage ParseMore(string json, string? walletCurrency)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return new BillingPage(WebPageState.LayoutChanged, [], null, 0);
        }
        using (parsed)
        {
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new BillingPage(WebPageState.LayoutChanged, [], null, 0);
            var cursor = root.TryGetProperty("cursor", out var value) ? ReadCursor(value) : null;
            var html = root.TryGetProperty("html", out var content) && content.ValueKind == JsonValueKind.String ? content.GetString() : null;
            if (string.IsNullOrWhiteSpace(html)) return new BillingPage(WebPageState.Ok, [], cursor, 0);
            // 这段 HTML 是 <tr> 片段，要放进表格才不会被解析器丢掉
            var document = new HtmlParser().ParseDocument($"<table><tbody>{html}</tbody></table>");
            var rows = document.QuerySelectorAll(".wallet_table_row");
            if (rows.Length == 0) return new BillingPage(WebPageState.LayoutChanged, [], cursor, 0);
            var currency = cursor == null ? walletCurrency : MoneyText.CurrencyOf(cursor.Currency) ?? walletCurrency;
            return ParseRows(rows, cursor, currency);
        }
    }

    private static BillingPage ParseRows(IHtmlCollection<IElement> rows, BillingCursor? cursor, string? walletCurrency)
    {
        var output = new List<BillingRow>(rows.Length);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var skipped = 0;
        foreach (var element in rows)
        {
            var row = ParseRow(element, walletCurrency);
            if (row == null)
            {
                skipped++;
                continue;
            }
            // 没有交易号的行（市场汇总）靠内容区分；同一天完全相同的两行再加序号
            var occurrence = seen.GetValueOrDefault(row.Key);
            seen[row.Key] = occurrence + 1;
            output.Add(occurrence == 0 ? row : row with { Key = $"{row.Key}#{occurrence}" });
        }
        return new BillingPage(WebPageState.Ok, output, cursor, skipped);
    }

    private static BillingRow? ParseRow(IElement element, string? walletCurrency)
    {
        var dateText = Text(element.QuerySelector(".wht_date"));
        if (!TryDate(dateText, out var date)) return null;

        var typeCell = element.QuerySelector(".wht_type");
        var typeText = FirstText(typeCell);
        if (typeText.Length == 0) return null;
        var payment = Text(typeCell?.QuerySelector(".wth_payment, .wht_payment"));
        var items = Lines(element.QuerySelector(".wht_items"));
        var totalCell = element.QuerySelector(".wht_total");
        var totalText = Text(totalCell);
        var total = MoneyText.Parse(totalText, walletCurrency);
        var change = MoneyText.Parse(Text(element.QuerySelector(".wht_wallet_change")), walletCurrency);
        var balance = MoneyText.Parse(Text(element.QuerySelector(".wht_wallet_balance")), walletCurrency);
        var refunded = (typeCell?.ClassList.Contains("wht_refunded") ?? false)
            || element.QuerySelector(".wht_refunded") != null
            || typeText.StartsWith("Refund", StringComparison.OrdinalIgnoreCase);
        var transactionId = ReadTransactionId(element);
        var kind = Classify(typeText, items, refunded);

        var key = transactionId != null
            ? $"txn:{transactionId}"
            : "row:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                string.Join('\u0001', date.ToString(CultureInfo.InvariantCulture), typeText, string.Join('\u0002', items), totalText, balance?.Text ?? string.Empty))))[..32];
        return new BillingRow(
            key,
            transactionId,
            date,
            kind,
            typeText,
            items,
            Math.Abs(total?.Cents ?? 0),
            total?.Currency ?? walletCurrency,
            totalText,
            change?.Cents,
            balance?.Cents,
            payment.Length == 0 ? null : payment,
            refunded);
    }

    private static string Classify(string type, IReadOnlyList<string> items, bool refunded)
    {
        var joined = string.Join(' ', items);
        if (type.Contains("Market", StringComparison.OrdinalIgnoreCase) || joined.Contains("Steam Community Market", StringComparison.OrdinalIgnoreCase)) return BillingKinds.Market;
        if (joined.Contains("Wallet Credit", StringComparison.OrdinalIgnoreCase) || joined.Contains("Steam Wallet", StringComparison.OrdinalIgnoreCase)
            || type.Contains("Wallet", StringComparison.OrdinalIgnoreCase) || type.StartsWith("Conversion", StringComparison.OrdinalIgnoreCase)) return BillingKinds.Wallet;
        if (type.StartsWith("Refund", StringComparison.OrdinalIgnoreCase)) return BillingKinds.Refund;
        if (type.StartsWith("In-Game", StringComparison.OrdinalIgnoreCase)) return BillingKinds.InGame;
        if (type.StartsWith("Gift", StringComparison.OrdinalIgnoreCase)) return BillingKinds.Gift;
        if (type.StartsWith("Purchase", StringComparison.OrdinalIgnoreCase)) return BillingKinds.Purchase;
        return refunded ? BillingKinds.Refund : BillingKinds.Other;
    }

    private static bool IsSignIn(IDocument document)
    {
        if (document.QuerySelector(".wallet_table_row") != null) return false;
        var title = document.Title?.Trim() ?? string.Empty;
        return title.Equals("Sign In", StringComparison.OrdinalIgnoreCase)
            || document.QuerySelector("#login_btn_signin, form[name='logon'], .login_modal") != null;
    }

    private static bool TryDate(string text, out long unix)
    {
        unix = 0;
        if (text.Length == 0) return false;
        if (!DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)) return false;
        unix = new DateTimeOffset(date.Date, TimeSpan.Zero).ToUnixTimeSeconds();
        return true;
    }

    private static string? ReadTransactionId(IElement element)
    {
        var direct = element.GetAttribute("data-transid") ?? element.GetAttribute("data-transactionid");
        if (!string.IsNullOrEmpty(direct)) return direct;
        var onclick = element.GetAttribute("onclick") ?? element.QuerySelector("[onclick*='transid=']")?.GetAttribute("onclick")
            ?? element.QuerySelector("a[href*='transid=']")?.GetAttribute("href");
        if (onclick == null) return null;
        var match = TransactionId().Match(onclick);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static BillingCursor? ReadCursor(string html)
    {
        var match = CursorScript().Match(html);
        if (!match.Success) return null;
        try
        {
            using var document = JsonDocument.Parse(match.Groups[1].Value);
            return ReadCursor(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static BillingCursor? ReadCursor(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        var txn = Scalar(element, "wallet_txnid");
        var newest = Scalar(element, "timestamp_newest");
        var balance = Scalar(element, "balance");
        var currency = Scalar(element, "currency");
        if (txn == null || !long.TryParse(newest, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timestamp)) return null;
        _ = int.TryParse(currency, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code);
        return new BillingCursor(txn, timestamp, balance ?? "0", code);
    }

    private static string? Scalar(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static string Text(IElement? element) => element == null ? string.Empty : Whitespace().Replace(element.TextContent, " ").Trim();

    /// <summary>类型单元格里后面跟着付款方式，只取第一段文字。</summary>
    private static string FirstText(IElement? element)
    {
        if (element == null) return string.Empty;
        foreach (var node in element.ChildNodes)
        {
            var value = Whitespace().Replace(node.TextContent, " ").Trim();
            if (value.Length > 0) return value;
        }
        return string.Empty;
    }

    private static IReadOnlyList<string> Lines(IElement? element)
    {
        if (element == null) return [];
        var children = element.Children.Where(child => child.LocalName is "div" or "span" or "a" or "li" or "p").ToArray();
        var values = children.Length > 0
            ? children.Select(Text)
            : element.TextContent.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(line => Whitespace().Replace(line, " ").Trim());
        return values.Where(line => line.Length > 0).ToArray();
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"transid=(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex TransactionId();

    [GeneratedRegex(@"g_historyCursor\s*=\s*(\{[^;]*?\})\s*;", RegexOptions.CultureInvariant)]
    private static partial Regex CursorScript();
}
