using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SteamStat.Core.Archives.Mining;

public sealed record MatchCandidate(int AppId, string Name);

/// <summary>同一天入库的一个许可证。AppIds 是这个包里的全部应用。</summary>
public sealed record LicenseOnDay(long Day, IReadOnlyList<int> AppIds);

public static class MatchSources
{
    public const string Exact = "exact";
    public const string Edition = "edition";
    public const string License = "license";
    public const string None = "none";
}

public sealed record PurchaseItemMatch(string Name, int? AppId, string Source, long AllocatedCents, bool Shared);

/// <summary>
/// 账单只有名字没有应用 ID。先按规范化后的名字匹配，再去掉版本后缀匹配；
/// 都不行时看同一天的许可证：只剩一个未匹配的商品和一个未匹配的应用时，它们就是一回事。
/// 一笔账单有多款游戏时金额平均分摊，标记 Shared，界面写「分摊」：账单不给单价。
/// </summary>
public static partial class PurchaseMatcher
{
    private const long Day = 86400;

    public static IReadOnlyList<PurchaseItemMatch> Match(
        IReadOnlyList<string> items,
        long date,
        long totalCents,
        IReadOnlyDictionary<string, int> exact,
        IReadOnlyDictionary<string, int> editions,
        IReadOnlyList<LicenseOnDay> licenses)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(exact);
        ArgumentNullException.ThrowIfNull(editions);
        ArgumentNullException.ThrowIfNull(licenses);
        if (items.Count == 0) return [];

        var appIds = new int?[items.Count];
        var sources = new string[items.Count];
        for (var index = 0; index < items.Count; index++)
        {
            var key = Normalize(items[index]);
            if (exact.TryGetValue(key, out var appId))
            {
                appIds[index] = appId;
                sources[index] = MatchSources.Exact;
            }
            else if (editions.TryGetValue(StripEdition(key), out appId))
            {
                appIds[index] = appId;
                sources[index] = MatchSources.Edition;
            }
            else
            {
                sources[index] = MatchSources.None;
            }
        }

        var unmatched = Enumerable.Range(0, items.Count).Where(index => appIds[index] == null).ToArray();
        if (unmatched.Length == 1)
        {
            var claimed = appIds.OfType<int>().ToHashSet();
            var sameDay = licenses
                .Where(license => Math.Abs(license.Day - date) <= Day)
                .SelectMany(license => license.AppIds)
                .Where(appId => appId > 0 && !claimed.Contains(appId))
                .Distinct()
                .ToArray();
            if (sameDay.Length == 1)
            {
                appIds[unmatched[0]] = sameDay[0];
                sources[unmatched[0]] = MatchSources.License;
            }
        }

        var shared = items.Count > 1;
        var each = totalCents / items.Count;
        var remainder = totalCents - each * items.Count;
        var output = new PurchaseItemMatch[items.Count];
        for (var index = 0; index < items.Count; index++)
            output[index] = new PurchaseItemMatch(items[index], appIds[index], sources[index], each + (index == 0 ? remainder : 0), shared);
        return output;
    }

    /// <summary>建匹配用的两张表。名字相同时留应用 ID 小的那个，通常是本体。</summary>
    public static (IReadOnlyDictionary<string, int> Exact, IReadOnlyDictionary<string, int> Editions) BuildIndex(IEnumerable<MatchCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var exact = new Dictionary<string, int>(StringComparer.Ordinal);
        var editions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var candidate in candidates.OrderBy(item => item.AppId))
        {
            var key = Normalize(candidate.Name);
            if (key.Length == 0) continue;
            exact.TryAdd(key, candidate.AppId);
            editions.TryAdd(StripEdition(key), candidate.AppId);
        }
        return (exact, editions);
    }

    public static string Normalize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var builder = new StringBuilder(name.Length);
        // 要在规范化之前去掉：NFKC 会把 ™ 变成字母 TM
        var bare = name.Replace("™", string.Empty, StringComparison.Ordinal).Replace("®", string.Empty, StringComparison.Ordinal).Replace("©", string.Empty, StringComparison.Ordinal);
        foreach (var character in bare.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
            else if (char.IsWhiteSpace(character) || character is '-' or ':' or '_' or '/') builder.Append(' ');
        }
        return Spaces().Replace(builder.ToString(), " ").Trim();
    }

    private static string StripEdition(string key) => Edition().Replace(key, string.Empty).Trim();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\s+(?:(?:digital\s+)?(?:deluxe|gold|ultimate|complete|premium|standard|definitive|enhanced|collectors?|anniversary|game of the year|goty|remastered|special|legendary|platinum)(?:\s+(?:edition|bundle|pack|version))?|edition|bundle)$", RegexOptions.CultureInvariant)]
    private static partial Regex Edition();
}
