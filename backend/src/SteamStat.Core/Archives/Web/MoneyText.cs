using System.Globalization;
using System.Text.RegularExpressions;

namespace SteamStat.Core.Archives.Web;

/// <summary>金额。Cents 永远是金额 × 100，日元这类没有小数的货币也一样。</summary>
public sealed record MoneyAmount(long Cents, string? Currency, string Text);

/// <summary>
/// 解析账单上的金额文字。不同货币写法差别很大：
/// ¥ 48.00、$19.99、19,99€、12,--€、CDN$ 12.34、1 234,56 pуб.、R$ 1.234,56、₩ 12,000、-¥ 12.00。
/// </summary>
public static partial class MoneyText
{
    /// <summary>Steam 的 ECurrencyCode。账单页的翻页游标里有钱包币种，¥ 这样有歧义的符号靠它判断。</summary>
    private static readonly Dictionary<int, string> WalletCurrencies = new()
    {
        [1] = "USD", [2] = "GBP", [3] = "EUR", [4] = "CHF", [5] = "RUB", [6] = "PLN", [7] = "BRL", [8] = "JPY",
        [9] = "NOK", [10] = "IDR", [11] = "MYR", [12] = "PHP", [13] = "SGD", [14] = "THB", [15] = "VND", [16] = "KRW",
        [17] = "TRY", [18] = "UAH", [19] = "MXN", [20] = "CAD", [21] = "AUD", [22] = "NZD", [23] = "CNY", [24] = "INR",
        [25] = "CLP", [26] = "PEN", [27] = "COP", [28] = "ZAR", [29] = "HKD", [30] = "TWD", [31] = "SAR", [32] = "AED",
        [34] = "ARS", [35] = "ILS", [37] = "KZT", [38] = "KWD", [39] = "QAR", [40] = "CRC", [41] = "UYU"
    };

    /// <summary>按长度从长到短匹配：ARS$ 里也有 S$，CDN$ 里也有 $。</summary>
    private static readonly (string Symbol, string Currency)[] Symbols = new (string Symbol, string Currency)[]
    {
        ("CDN$", "CAD"), ("A$", "AUD"), ("NZ$", "NZD"), ("HK$", "HKD"), ("NT$", "TWD"), ("S$", "SGD"), ("R$", "BRL"),
        ("Mex$", "MXN"), ("ARS$", "ARS"), ("COL$", "COP"), ("CLP$", "CLP"), ("$U", "UYU"), ("S/.", "PEN"),
        ("CHF", "CHF"), ("kr", "NOK"), ("zł", "PLN"), ("pуб", "RUB"), ("руб", "RUB"), ("TL", "TRY"), ("₺", "TRY"),
        ("€", "EUR"), ("£", "GBP"), ("₩", "KRW"), ("₴", "UAH"), ("₫", "VND"), ("₹", "INR"), ("₪", "ILS"), ("₸", "KZT"),
        ("₡", "CRC"), ("฿", "THB"), ("₱", "PHP"), ("Rp", "IDR"), ("RM", "MYR"), ("SR", "SAR"), ("AED", "AED"), ("KD", "KWD"),
        ("QR", "QAR")
    }.OrderByDescending(entry => entry.Symbol.Length).ToArray();

    public static string? CurrencyOf(int walletCurrency) => WalletCurrencies.GetValueOrDefault(walletCurrency);

    public static MoneyAmount? Parse(string? text, string? walletCurrency = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var normalized = text.Replace(' ', ' ').Replace('−', '-').Trim();
        var match = Amount().Match(normalized);
        if (!match.Success) return null;
        if (!TryCents(match.Value, out var cents)) return null;
        var before = normalized[..match.Index];
        var after = normalized[(match.Index + match.Length)..];
        var negative = before.Contains('-') || before.Contains('(');
        return new MoneyAmount(negative ? -cents : cents, ResolveCurrency(before + " " + after, walletCurrency), normalized);
    }

    private static string? ResolveCurrency(string symbolText, string? walletCurrency)
    {
        var symbol = symbolText.Replace("-", string.Empty, StringComparison.Ordinal).Trim();
        if (symbol == "R") return "ZAR";
        foreach (var (known, currency) in Symbols)
        {
            if (symbol.Contains(known, StringComparison.Ordinal)) return currency;
        }
        // ¥ 同时是人民币和日元，$ 是十几种货币，这些只能相信钱包币种
        if (symbol.Contains('¥') || symbol.Contains('￥')) return walletCurrency is "CNY" or "JPY" ? walletCurrency : "CNY";
        if (symbol.Contains('$')) return walletCurrency ?? "USD";
        return walletCurrency;
    }

    private static bool TryCents(string text, out long cents)
    {
        cents = 0;
        var value = text.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("'", string.Empty, StringComparison.Ordinal).Replace("’", string.Empty, StringComparison.Ordinal);
        // 12,--€：欧元区整数价格的写法
        var fraction = string.Empty;
        if (value.EndsWith("--", StringComparison.Ordinal)) value = value[..^3];
        var separator = value.LastIndexOfAny(['.', ',']);
        if (separator >= 0)
        {
            var tail = value[(separator + 1)..];
            // 后面正好三位是千分位：₩ 12,000、¥ 1,200、1.234
            if (tail.Length is 1 or 2)
            {
                fraction = tail;
                value = value[..separator];
            }
        }
        value = value.Replace(".", string.Empty, StringComparison.Ordinal).Replace(",", string.Empty, StringComparison.Ordinal);
        if (value.Length == 0) value = "0";
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var whole) || whole > long.MaxValue / 1000) return false;
        var decimals = fraction.PadRight(2, '0');
        cents = whole * 100 + int.Parse(decimals, CultureInfo.InvariantCulture);
        return true;
    }

    [GeneratedRegex(@"\d[\d\s.,'’]*(?:--)?", RegexOptions.CultureInvariant)]
    private static partial Regex Amount();
}
