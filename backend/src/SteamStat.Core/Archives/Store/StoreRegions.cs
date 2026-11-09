using System.Text.RegularExpressions;

namespace SteamStat.Core.Archives.Store;

/// <summary>
/// 商店地区。价格按地区不同，币种也不同：购前模拟要算用户实际要付的钱，所以要知道他在哪个区。
/// 每周的商店状态检查还是固定用美区，那里要的是所有用户之间可比。
/// </summary>
public static partial class StoreRegions
{
    public const string Default = "US";

    /// <summary>货币 → 代表地区。账单只有币种，同一币种的地区价格基本一致。</summary>
    private static readonly Dictionary<string, string> CurrencyRegions = new(StringComparer.Ordinal)
    {
        ["USD"] = "US", ["CNY"] = "CN", ["JPY"] = "JP", ["KRW"] = "KR", ["HKD"] = "HK", ["TWD"] = "TW", ["SGD"] = "SG",
        ["MYR"] = "MY", ["THB"] = "TH", ["IDR"] = "ID", ["PHP"] = "PH", ["VND"] = "VN", ["INR"] = "IN", ["GBP"] = "GB",
        ["EUR"] = "DE", ["RUB"] = "RU", ["UAH"] = "UA", ["TRY"] = "TR", ["BRL"] = "BR", ["ARS"] = "AR", ["MXN"] = "MX",
        ["CAD"] = "CA", ["AUD"] = "AU", ["NZD"] = "NZ", ["ZAR"] = "ZA", ["PLN"] = "PL", ["NOK"] = "NO", ["CHF"] = "CH",
        ["ILS"] = "IL", ["SAR"] = "SA", ["AED"] = "AE", ["KZT"] = "KZ", ["KWD"] = "KW", ["QAR"] = "QA", ["CLP"] = "CL",
        ["COP"] = "CO", ["PEN"] = "PE", ["CRC"] = "CR", ["UYU"] = "UY"
    };

    /// <summary>不合法的值返回 null，调用方继续往下找。</summary>
    public static string? Normalize(string? value)
    {
        var region = value?.Trim().ToUpperInvariant();
        return region != null && Code().IsMatch(region) ? region : null;
    }

    public static string? OfCurrency(string? currency)
        => currency == null ? null : CurrencyRegions.GetValueOrDefault(currency);

    /// <summary>最后的退路：什么都不知道时看界面语言。</summary>
    public static string OfLanguage(string steamLanguage) => steamLanguage switch
    {
        "schinese" => "CN",
        "tchinese" => "TW",
        "japanese" => "JP",
        "koreana" => "KR",
        "russian" => "RU",
        _ => Default
    };

    /// <summary>
    /// 商店只给「¥35.00」这样的文字，不给币种代码。¥ 同时是人民币和日元，日本区要提示一下，
    /// 其他符号 MoneyText 自己认得出来。
    /// </summary>
    public static string? CurrencyHint(string region) => region == "JP" ? "JPY" : null;

    [GeneratedRegex("^[A-Z]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex Code();
}
