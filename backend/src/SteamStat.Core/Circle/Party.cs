using SteamStat.Core.Insights;

namespace SteamStat.Core.Circle;

public sealed record PartyOwnership(int Minutes, int RecentMinutes, long LastPlayed);

/// <summary>开黑的一位参与者。Library 为空表示不知道（游戏库私密或没读到），Forecast 为空表示没有作息数据。</summary>
public sealed record PartyMember(string SteamId, bool IsMe, IReadOnlyDictionary<int, PartyOwnership>? Library, double?[]? Forecast);

public sealed record PartyGame(
    int AppId,
    /// <summary>标签确认能联机。没有标签的游戏也列出来，但不算确认。</summary>
    bool Verified,
    IReadOnlyList<string> Owners,
    IReadOnlyList<string> Missing,
    IReadOnlyDictionary<string, int> Minutes,
    int RecentPlayers,
    double Score);

/// <summary>连续的一段时段。Probability 是所有人都在线的概率（假设彼此独立），Expected 是预计在线人数。</summary>
public sealed record PartySlot(int Weekday, int StartHour, int EndHour, double Probability, double Expected);

public sealed record PartyPlan(
    IReadOnlyList<PartyGame> Common,
    IReadOnlyList<PartyGame> NearMiss,
    IReadOnlyList<PartySlot> Slots,
    /// <summary>168 格，每格所有人都在线的概率；有人没有那一格的数据时为空。</summary>
    IReadOnlyList<double?> Together,
    /// <summary>168 格，预计在线人数。</summary>
    IReadOnlyList<double> Expected,
    IReadOnlyList<string> UnknownLibraries,
    IReadOnlyList<string> NoForecast);

/// <summary>
/// 开黑助手：大家都有的联机游戏、只差一两个人的游戏、最可能同时在线的时段。
/// 不需要对方安装 Steam Stat：游戏库来自 Steam 公开资料，作息来自手动追踪的好友记录。
/// </summary>
public static class PartyPlanner
{
    public const int MaxGames = 30;
    /// <summary>「差几个人」最多差这么多。</summary>
    public const int MaxMissing = 2;
    private const double SlotShare = 0.75;

    public static PartyPlan Plan(IReadOnlyList<PartyMember> members, IReadOnlyDictionary<int, InsightAppFacts> apps)
    {
        var known = members.Where(member => member.Library != null).ToList();
        var common = new List<PartyGame>();
        var nearMiss = new List<PartyGame>();
        if (known.Count >= 2)
        {
            foreach (var appId in known.SelectMany(member => member.Library!.Keys).Distinct())
            {
                var tags = apps.GetValueOrDefault(appId)?.Tags ?? [];
                if (tags.Count > 0 && !SessionTagger.CanPlayTogether(tags)) continue;
                var owners = known.Where(member => member.Library!.ContainsKey(appId)).ToList();
                var missing = known.Where(member => !member.Library!.ContainsKey(appId)).Select(member => member.SteamId).ToList();
                if (owners.Count < 2) continue;
                var minutes = owners.ToDictionary(member => member.SteamId, member => member.Library![appId].Minutes);
                var recent = owners.Count(member => member.Library![appId].RecentMinutes > 0);
                var score = owners.Sum(member => Math.Log(1 + member.Library![appId].Minutes)) + recent * 2 + (tags.Count > 0 ? 1 : 0);
                var game = new PartyGame(appId, tags.Count > 0, owners.Select(member => member.SteamId).ToList(), missing, minutes, recent, Math.Round(score, 3));
                if (missing.Count == 0) common.Add(game);
                else if (missing.Count <= MaxMissing && owners.Count >= Math.Max(2, known.Count - MaxMissing)) nearMiss.Add(game);
            }
        }

        var forecasts = members.Where(member => member.Forecast != null).ToList();
        var together = new double?[WeekHours.Cells];
        var expected = new double[WeekHours.Cells];
        for (var cell = 0; cell < WeekHours.Cells; cell++)
        {
            double product = 1;
            var complete = forecasts.Count > 0;
            foreach (var member in forecasts)
            {
                var value = member.Forecast![cell];
                if (value is { } probability)
                {
                    product *= probability;
                    if (!member.IsMe) expected[cell] += probability;
                }
                else complete = false;
            }
            together[cell] = complete ? product : null;
        }

        return new PartyPlan(
            common.OrderByDescending(game => game.Score).ThenBy(game => game.AppId).Take(MaxGames).ToList(),
            nearMiss.OrderBy(game => game.Missing.Count).ThenByDescending(game => game.Score).Take(MaxGames).ToList(),
            Slots(together, expected),
            together.Select(value => value is { } v ? (double?)Math.Round(v, 4) : null).ToList(),
            expected.Select(value => Math.Round(value, 3)).ToList(),
            members.Where(member => member.Library == null).Select(member => member.SteamId).ToList(),
            members.Where(member => member.Forecast == null).Select(member => member.SteamId).ToList());
    }

    /// <summary>
    /// 先按「都在线的概率」找最好的格子，取不低于最好值 75% 的格子，同一天连着的合并成一段，最多三段。
    /// 没有完整概率时退回到预计在线人数。
    /// </summary>
    internal static IReadOnlyList<PartySlot> Slots(IReadOnlyList<double?> together, IReadOnlyList<double> expected)
    {
        var usesProbability = together.Any(value => value is > 0);
        double ScoreOf(int cell) => usesProbability ? together[cell] ?? 0 : expected[cell];
        var best = Enumerable.Range(0, WeekHours.Cells).Max(ScoreOf);
        if (best <= 0) return [];
        var slots = new List<(PartySlot Slot, double Score)>();
        for (var weekday = 0; weekday < 7; weekday++)
        {
            var hour = 0;
            while (hour < 24)
            {
                var cell = weekday * 24 + hour;
                if (ScoreOf(cell) < best * SlotShare)
                {
                    hour++;
                    continue;
                }
                var start = hour;
                while (hour < 24 && ScoreOf(weekday * 24 + hour) >= best * SlotShare) hour++;
                var cells = Enumerable.Range(start, hour - start).Select(h => weekday * 24 + h).ToList();
                var probability = cells.Average(c => together[c] ?? 0);
                var count = cells.Average(c => expected[c]);
                slots.Add((new PartySlot(weekday, start, hour, Math.Round(probability, 4), Math.Round(count, 2)), cells.Average(ScoreOf)));
            }
        }
        return slots.OrderByDescending(slot => slot.Score).Take(3).Select(slot => slot.Slot).ToList();
    }
}
