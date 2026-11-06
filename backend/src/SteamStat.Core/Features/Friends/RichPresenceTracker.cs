using System.Text.Json;

namespace SteamStat.Core.Features.Friends;

public enum RichPresenceSource
{
    /// <summary>好友状态推送（ClientPersonaState）。</summary>
    Push,
    /// <summary>主动请求的回复（ClientRichPresenceInfo）：会话开始、换游戏和定时对账。</summary>
    Poll
}

public sealed record RichPresenceObservation(
    ulong SteamId,
    uint? AppId,
    IReadOnlyDictionary<string, string> Values,
    RichPresenceSource Source,
    long ObservedAt);

/// <summary>上次运行时最后落库的状态，用来避免重启后把没变的状态再记一遍。</summary>
public sealed record RichPresenceBaseline(ulong SteamId, uint AppId, string RawValues, string? DisplayText, long RecordId);

public sealed class RichPresenceEntryState
{
    public required uint AppId { get; init; }
    public required string RawValues { get; init; }
    public required string ComparisonKey { get; init; }
    public required IReadOnlyDictionary<string, string> Values { get; init; }
    public string? Text { get; set; }
    public long? RecordId { get; set; }
    public bool FromBaseline { get; init; }
    public long ObservedAt { get; init; }
    /// <summary>连续收到的空回复数。</summary>
    public int EmptyPolls { get; set; }
    public bool IsEmpty => Values.Count == 0;
}

public sealed record RichPresenceDecision(
    RichPresenceObservation Observation,
    RichPresenceEntryState Current,
    RichPresenceEntryState? Previous,
    bool Changed);

/// <summary>
/// 游戏内状态的状态机。只做判断，不碰网络和数据库，由 <see cref="RichPresenceCollector"/> 在单一线程上驱动。
/// <list type="bullet">
/// <item>判断变化比较的是原始键值，不是解析后的文案：文案依赖本地化，拿不到时不能把变化丢掉。</item>
/// <item>推送和轮询走同一条路径，谁先看到都一样，只差来源标记。</item>
/// <item>状态可能先于「好友开始玩这款游戏」到达，这时先缓存，等知道游戏再处理。</item>
/// </list>
/// </summary>
public sealed class RichPresenceTracker
{
    /// <summary>
    /// 这些键的变化不算状态变化，但仍保存在原始键值里。connect 是每局重新生成的加入令牌，
    /// 参与比较的话同一个状态会被反复记录。
    /// </summary>
    private static readonly HashSet<string> IgnoredInComparison = new(StringComparer.OrdinalIgnoreCase) { "connect" };

    /// <summary>
    /// 连续这么多次空回复才算状态被清空。一次空回复不够：如果轮询和推送给出的内容不一致，
    /// 单次判定会让状态在有和无之间反复横跳，每次都留下一条记录。
    /// </summary>
    internal const int EmptyPollsToClear = 2;

    private readonly Dictionary<ulong, RichPresenceEntryState> _entries = [];
    private readonly Dictionary<ulong, uint> _games = [];
    private readonly Dictionary<ulong, RichPresenceObservation> _pending = [];

    public int Count => _entries.Values.Count(entry => !entry.IsEmpty);

    public static string Canonicalize(IReadOnlyDictionary<string, string> values)
        => JsonSerializer.Serialize(values
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase));

    private static string ComparisonKeyOf(IReadOnlyDictionary<string, string> values)
        => JsonSerializer.Serialize(values
            .Where(item => !IgnoredInComparison.Contains(item.Key))
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase));

    public void Seed(IEnumerable<RichPresenceBaseline> baselines)
    {
        foreach (var baseline in baselines)
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(baseline.RawValues) ?? [];
            _entries[baseline.SteamId] = new RichPresenceEntryState
            {
                AppId = baseline.AppId,
                RawValues = baseline.RawValues,
                ComparisonKey = ComparisonKeyOf(values),
                Values = values,
                Text = baseline.DisplayText,
                RecordId = baseline.RecordId,
                FromBaseline = true
            };
        }
    }

    /// <summary>处理一次观测。还不知道这个人在玩什么时返回 null，等 <see cref="GameChanged"/> 再处理。</summary>
    public RichPresenceDecision? Apply(RichPresenceObservation observation)
    {
        var steamId = observation.SteamId;
        var appId = observation.AppId ?? _games.GetValueOrDefault(steamId);
        if (appId == 0)
        {
            // 没有游戏时的空状态没有任何信息，不用等
            if (observation.Values.Count > 0) _pending[steamId] = observation;
            return null;
        }
        _games[steamId] = appId;
        _pending.Remove(steamId);

        var key = ComparisonKeyOf(observation.Values);
        _entries.TryGetValue(steamId, out var previous);
        if (previous != null && previous.AppId == appId && previous.ComparisonKey == key)
        {
            previous.EmptyPolls = 0;
            return new RichPresenceDecision(observation, previous, previous, false);
        }
        if (observation.Values.Count == 0 && previous is { IsEmpty: false } && previous.AppId == appId
            && ++previous.EmptyPolls < EmptyPollsToClear)
            return null;

        // 换到新游戏后的第一条空状态什么都没说明
        if (observation.Values.Count == 0 && (previous == null || previous.IsEmpty || previous.AppId != appId))
        {
            _entries.Remove(steamId);
            return null;
        }

        var current = new RichPresenceEntryState
        {
            AppId = appId,
            RawValues = Canonicalize(observation.Values),
            ComparisonKey = key,
            Values = observation.Values,
            ObservedAt = observation.ObservedAt
        };
        _entries[steamId] = current;
        return new RichPresenceDecision(observation, current, previous, true);
    }

    /// <summary>这个人的游戏变了。返回之前缓存、现在可以处理的观测。</summary>
    public RichPresenceDecision? GameChanged(ulong steamId, uint appId)
    {
        _games[steamId] = appId;
        if (_entries.TryGetValue(steamId, out var entry) && entry.AppId != appId) _entries.Remove(steamId);
        if (appId == 0)
        {
            _pending.Remove(steamId);
            return null;
        }
        return _pending.Remove(steamId, out var observation) ? Apply(observation) : null;
    }
}
