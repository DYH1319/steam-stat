namespace SteamStat.Core.Circle;

public static class AdoptionEvidenceKinds
{
    /// <summary>看到了第一次玩（会话记录，或开始记录之后好友第一次出现在这款游戏里且之前的快照里没有它）。</summary>
    public const string Recorded = "recorded";
    /// <summary>两次快照之间出现的：时间精确到采集间隔。</summary>
    public const string Inferred = "inferred";
    /// <summary>开始记录时就已经有了，只知道不晚于这个时间。</summary>
    public const string Before = "before";
}

/// <summary>某人开始玩某款游戏的一条证据。Before 的 Time 是上限。</summary>
public sealed record AdoptionEvidence(string SteamId, int AppId, long Time, string Evidence);

/// <summary>某人在玩某款游戏的一段时间，用来找「谁带谁入坑」。</summary>
public sealed record ActivitySpan(string SteamId, int AppId, long Start, long End);

public sealed record Adopter(
    string SteamId,
    long Time,
    string Evidence,
    /// <summary>入坑前 30 天里玩这款游戏最多的、更早入坑的人。</summary>
    string? InfluencedBy,
    long InfluenceSeconds);

public sealed record AdoptionChain(int AppId, IReadOnlyList<Adopter> Adopters, int? MyRank)
{
    public int Count => Adopters.Count;
}

/// <summary>
/// 入坑传播：圈子里谁先玩、谁跟上。只有手动追踪的好友有记录，所以这是「我看得到的那部分圈子」。
/// 好友之间是不是互相认识这里不知道，影响关系只说明时间上的先后和当时谁在玩。
/// </summary>
public static class Adoption
{
    public const long InfluenceWindowSeconds = 30 * 86400L;
    public const long MinInfluenceSeconds = 30 * 60L;

    public static AdoptionChain Chain(int appId, IEnumerable<AdoptionEvidence> evidence, IReadOnlyList<ActivitySpan> activity, string me)
    {
        var people = evidence.Where(item => item.AppId == appId)
            .GroupBy(item => item.SteamId)
            .Select(group =>
            {
                var before = group.Where(item => item.Evidence == AdoptionEvidenceKinds.Before).ToList();
                var earliest = group.MinBy(item => item.Time)!;
                // 开始记录时就有了：确切时间不知道，最早的那条只是上限
                return before.Count > 0
                    ? new { group.Key, Time = Math.Min(before.Min(item => item.Time), earliest.Time), Evidence = AdoptionEvidenceKinds.Before }
                    : new { group.Key, earliest.Time, earliest.Evidence };
            })
            .OrderBy(item => item.Evidence == AdoptionEvidenceKinds.Before ? 0 : 1)
            .ThenBy(item => item.Time)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .ToList();

        var spans = activity.Where(span => span.AppId == appId).ToLookup(span => span.SteamId);
        var adopters = new List<Adopter>();
        foreach (var person in people)
        {
            string? influencer = null;
            long influence = 0;
            if (person.Evidence != AdoptionEvidenceKinds.Before)
            {
                var window = new Span(person.Time - InfluenceWindowSeconds, person.Time);
                foreach (var earlier in adopters)
                {
                    var seconds = Spans.Total(spans[earlier.SteamId].Select(span => window.Intersect(new Span(span.Start, span.End))).OfType<Span>());
                    if (seconds >= MinInfluenceSeconds && seconds > influence)
                    {
                        influencer = earlier.SteamId;
                        influence = seconds;
                    }
                }
            }
            adopters.Add(new Adopter(person.Key, person.Time, person.Evidence, influencer, influence));
        }
        var rank = adopters.FindIndex(adopter => adopter.SteamId == me);
        return new AdoptionChain(appId, adopters, rank < 0 ? null : rank + 1);
    }

    /// <summary>圈子里至少两个人玩过、且至少有一个人的入坑时间是看到的游戏，按人数排序。</summary>
    public static IReadOnlyList<AdoptionChain> Overview(IReadOnlyList<AdoptionEvidence> evidence, IReadOnlyList<ActivitySpan> activity, string me, int limit = 60)
    {
        var activityByApp = activity.ToLookup(span => span.AppId);
        return evidence.GroupBy(item => item.AppId)
            .Where(group => group.Select(item => item.SteamId).Distinct().Count() >= 2)
            .Select(group => Chain(group.Key, group, activityByApp[group.Key].ToList(), me))
            .Where(chain => chain.Adopters.Any(adopter => adopter.Evidence != AdoptionEvidenceKinds.Before))
            .OrderByDescending(chain => chain.Count)
            .ThenByDescending(chain => chain.Adopters.Max(adopter => adopter.Time))
            .Take(limit)
            .ToList();
    }
}
