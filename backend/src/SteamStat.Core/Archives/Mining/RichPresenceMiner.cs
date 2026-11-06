using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamStat.Core.Archives.Mining;

public static class RichPresenceDimensions
{
    public const string Map = "map";
    public const string Mode = "mode";
    public const string Rank = "rank";
    public const string Character = "character";
    public const string Difficulty = "difficulty";
    public const string Activity = "activity";
    public const string Party = "party";
}

/// <summary>一次状态变化。RawValues 是原始键值的 JSON，空对象表示状态被清空。</summary>
public sealed record RichPresenceSample(long ObservedAt, string RawValues, string? DisplayText);

public sealed record RichPresenceValue(string Value, long Seconds, int Times, long FirstAt, long LastAt);

public sealed record RichPresenceDimension(string Kind, string Key, IReadOnlyList<RichPresenceValue> Values);

/// <summary>段位这类有先后的值，什么时候变成了什么。</summary>
public sealed record RichPresenceChange(long At, string Value);

public sealed record RichPresenceReport(
    long RecordedSeconds,
    int Records,
    long? FirstAt,
    long? LastAt,
    IReadOnlyList<RichPresenceDimension> Dimensions,
    IReadOnlyList<RichPresenceChange> RankHistory,
    IReadOnlyList<string> UnknownKeys);

/// <summary>
/// 把原始键值解析成地图、模式、段位。游戏用的键名五花八门，没有统一规范，所以按键名归类，
/// 不认识的键列出来而不是丢掉，原始记录一直都在，规则以后可以补。
/// 每条记录的时长是到下一条记录为止，最长算 6 小时：应用退出或掉线时没有结束记录。
/// </summary>
public static partial class RichPresenceMiner
{
    private const long MaxSegmentSeconds = 6 * 3600;
    private const int MaxValues = 30;
    private const int MaxRankChanges = 200;

    /// <summary>协议字段和纯展示字段，不是游戏内容。</summary>
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "steam_display", "connect", "steam_player_group", "steam_player_group_size",
        "game:state", "game:act", "game:score", "score", "time", "timestamp", "version", "build", "lobby", "lobbyid",
        "watching", "watchablegameid", "watching_server", "watching_from_server", "party", "partyid", "party_id",
        "steamid", "accountid", "player_count", "players", "num_players", "max_players"
    };

    public static RichPresenceReport Mine(IReadOnlyList<RichPresenceSample> samples, long now)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var ordered = samples.OrderBy(sample => sample.ObservedAt).ToArray();
        var totals = new Dictionary<(string Kind, string Key), Dictionary<string, Accumulator>>();
        var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rankChanges = new List<RichPresenceChange>();
        string? lastRank = null;
        long recorded = 0;

        for (var index = 0; index < ordered.Length; index++)
        {
            var sample = ordered[index];
            var values = ParseValues(sample.RawValues);
            if (values.Count == 0) continue;
            var end = index + 1 < ordered.Length ? ordered[index + 1].ObservedAt : now;
            var seconds = Math.Clamp(end - sample.ObservedAt, 0, MaxSegmentSeconds);
            recorded += seconds;

            foreach (var (key, raw) in values)
            {
                var name = key.StartsWith("game:", StringComparison.OrdinalIgnoreCase) ? key[5..] : key;
                if (Ignored.Contains(key) || Ignored.Contains(name)) continue;
                var kind = Classify(name);
                if (kind == null)
                {
                    if (!IsParameter(name)) unknown.Add(key);
                    continue;
                }
                var value = Clean(raw, kind);
                if (value == null) continue;
                Add(totals, kind, key, value, sample.ObservedAt, seconds);
                if (kind == RichPresenceDimensions.Rank && value != lastRank)
                {
                    rankChanges.Add(new RichPresenceChange(sample.ObservedAt, value));
                    lastRank = value;
                }
            }

            if (values.TryGetValue("steam_player_group_size", out var size) && int.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out var players) && players > 0)
                Add(totals, RichPresenceDimensions.Party, "steam_player_group_size", players.ToString(CultureInfo.InvariantCulture), sample.ObservedAt, seconds);

            // 不管游戏用什么键，显示给好友看的那句话总是有的。数字换成 #，比分变化时才能归成一类
            if (!string.IsNullOrWhiteSpace(sample.DisplayText))
                Add(totals, RichPresenceDimensions.Activity, "steam_display", Digits().Replace(sample.DisplayText.Trim(), "#"), sample.ObservedAt, seconds);
        }

        var dimensions = totals
            .Select(entry => new RichPresenceDimension(entry.Key.Kind, entry.Key.Key, Top(entry.Value)))
            .OrderBy(dimension => Order(dimension.Kind))
            .ThenByDescending(dimension => dimension.Values.Sum(value => value.Seconds))
            .ToArray();
        return new RichPresenceReport(
            recorded,
            ordered.Length,
            ordered.Length == 0 ? null : ordered[0].ObservedAt,
            ordered.Length == 0 ? null : ordered[^1].ObservedAt,
            dimensions,
            rankChanges.TakeLast(MaxRankChanges).ToArray(),
            unknown.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static IReadOnlyList<RichPresenceValue> Top(Dictionary<string, Accumulator> values)
        => values.Select(entry => new RichPresenceValue(entry.Key, entry.Value.Seconds, entry.Value.Times, entry.Value.FirstAt, entry.Value.LastAt))
            .OrderByDescending(value => value.Seconds)
            .ThenBy(value => value.Value, StringComparer.Ordinal)
            .Take(MaxValues)
            .ToArray();

    private static void Add(Dictionary<(string, string), Dictionary<string, Accumulator>> totals, string kind, string key, string value, long at, long seconds)
    {
        if (!totals.TryGetValue((kind, key), out var values)) totals[(kind, key)] = values = new Dictionary<string, Accumulator>(StringComparer.Ordinal);
        if (!values.TryGetValue(value, out var accumulator)) values[value] = accumulator = new Accumulator(at);
        accumulator.Seconds += seconds;
        accumulator.Times++;
        accumulator.LastAt = at;
    }

    private static string? Classify(string key)
    {
        var name = key.ToLowerInvariant();
        if (MapKey().IsMatch(name)) return RichPresenceDimensions.Map;
        if (ModeKey().IsMatch(name)) return RichPresenceDimensions.Mode;
        if (RankKey().IsMatch(name)) return RichPresenceDimensions.Rank;
        if (CharacterKey().IsMatch(name)) return RichPresenceDimensions.Character;
        if (DifficultyKey().IsMatch(name)) return RichPresenceDimensions.Difficulty;
        return null;
    }

    /// <summary>param0 这样的键是文案模板的参数，本身没有含义。</summary>
    private static bool IsParameter(string key) => Parameter().IsMatch(key);

    /// <summary>「#Mode_Competitive」是本地化键，取最后一段。数字和十六进制 ID 只有段位才有意义。</summary>
    private static string? Clean(string raw, string kind)
    {
        var value = raw.Trim();
        if (value.Length == 0 || value.Length > 80) return null;
        if (value.StartsWith('#'))
        {
            var separator = value.LastIndexOf('_');
            value = (separator >= 0 ? value[(separator + 1)..] : value[1..]).Trim();
            if (value.Length == 0) return null;
        }
        if (kind != RichPresenceDimensions.Rank && (AllDigits().IsMatch(value) || HexId().IsMatch(value))) return null;
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase) || value.Equals("null", StringComparison.OrdinalIgnoreCase)) return null;
        return value;
    }

    private static int Order(string kind) => kind switch
    {
        RichPresenceDimensions.Map => 0,
        RichPresenceDimensions.Mode => 1,
        RichPresenceDimensions.Rank => 2,
        RichPresenceDimensions.Character => 3,
        RichPresenceDimensions.Difficulty => 4,
        RichPresenceDimensions.Party => 5,
        _ => 6
    };

    private static Dictionary<string, string> ParseValues(string json)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return values;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String) values[property.Name] = property.Value.GetString() ?? string.Empty;
                else if (property.Value.ValueKind == JsonValueKind.Number) values[property.Name] = property.Value.GetRawText();
            }
        }
        catch (JsonException)
        {
        }
        return values;
    }

    private sealed class Accumulator(long firstAt)
    {
        public long Seconds { get; set; }
        public int Times { get; set; }
        public long FirstAt { get; } = firstAt;
        public long LastAt { get; set; } = firstAt;
    }

    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant)]
    private static partial Regex Digits();

    [GeneratedRegex(@"^(?:current_?)?(?:map(?:_?name)?|level(?:_?name)|zone|area|location|region|world|stage|arena|mission|chapter|planet|biome)$", RegexOptions.CultureInvariant)]
    private static partial Regex MapKey();

    [GeneratedRegex(@"^(?:current_?)?(?:(?:game|match|play)?_?mode|matchgroup(?:loc)?|playlist|queue|ruleset|gametype|game_type)$", RegexOptions.CultureInvariant)]
    private static partial Regex ModeKey();

    [GeneratedRegex(@"^(?:current_?)?(?:rank(?:ing|ed)?|skill(?:_?group)?|elo|mmr|tier|league|division|prestige)$", RegexOptions.CultureInvariant)]
    private static partial Regex RankKey();

    [GeneratedRegex(@"^(?:current_?)?(?:hero(?:_?name)?|character|class|champion|legend|agent|faction|civilization|civ|race)$", RegexOptions.CultureInvariant)]
    private static partial Regex CharacterKey();

    [GeneratedRegex(@"^difficulty(?:_?level)?$", RegexOptions.CultureInvariant)]
    private static partial Regex DifficultyKey();

    [GeneratedRegex(@"^(?:param|arg|p)\d*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Parameter();

    [GeneratedRegex(@"^\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex AllDigits();

    [GeneratedRegex(@"^[0-9a-fA-F]{12,}$", RegexOptions.CultureInvariant)]
    private static partial Regex HexId();
}
