namespace SteamStat.Core.Insights;

/// <summary>人格的一条轴。Score 在 -1~1 之间，正值是前一个字母。</summary>
public sealed record PersonaAxis(string Id, double Score, string Letter, double Value);

public sealed record PersonaTitle(Bilingual Text, IReadOnlyList<Bilingual> Reasons);

/// <summary>一年的玩家人格和称号。全部由真实数据算出，所以每年会变。</summary>
public sealed record PlayerPersona(int Year, string Code, Bilingual Name, Bilingual Summary, IReadOnlyList<PersonaAxis> Axes, PersonaTitle Title, int Sessions, double Hours);

/// <summary>
/// 玩家人格 16 型：探索 / 专精、社交 / 独行、夜 / 昼、爆发 / 稳定。
/// 阈值是经验值，落在中线附近的轴本来就不明显，界面显示分数而不是只给一个标签。
/// </summary>
public static class PersonaAnalyzer
{
    public const int MinimumSessions = 30;
    public const double MinimumHours = 20;

    private static readonly IReadOnlyDictionary<string, (Bilingual Name, Bilingual Summary)> Types = new Dictionary<string, (Bilingual, Bilingual)>
    {
        ["ESNB"] = (new("午夜派对发起人", "Midnight Party Starter"), new("夜里拉一群人开新游戏，三天后换下一款。", "Rounds up a crowd for something new at midnight, then moves on three days later.")),
        ["ESNT"] = (new("深夜茶话会常客", "Night Salon Regular"), new("每晚准时上线，什么都玩一点，主要是为了那几个人。", "Online every night, a little of everything, mostly for the company.")),
        ["ESDB"] = (new("周末远征队长", "Weekend Expedition Leader"), new("白天集结，一口气打穿，下周换一片大陆。", "Musters by day, pushes straight through, picks a new continent next week.")),
        ["ESDT"] = (new("社区常驻代表", "Community Regular"), new("作息规律，游戏广泛，好友列表里最靠谱的那一位。", "Regular hours, broad tastes, the reliable name on the friends list.")),
        ["ELNB"] = (new("午夜拓荒者", "Midnight Pioneer"), new("夜深人静时独自开荒，开完就走。", "Breaks new ground alone after dark, then leaves it behind.")),
        ["ELNT"] = (new("深夜漫游者", "Night Wanderer"), new("每晚一个人逛一点，没有目的地，也不赶路。", "Strolls alone each night, no destination, no hurry.")),
        ["ELDB"] = (new("白昼突袭者", "Daylight Raider"), new("大白天突然打开一款新游戏，打完收工。", "Opens something new in broad daylight, finishes, clocks out.")),
        ["ELDT"] = (new("独行观光客", "Solo Sightseer"), new("按部就班地看遍各种世界，不打扰任何人。", "Works through one world after another at a steady pace, bothering no one.")),
        ["FSNB"] = (new("凌晨开黑指挥官", "Late Shift Commander"), new("一款游戏，一群人，一直打到天亮，然后歇一阵。", "One game, one squad, until dawn, then a long rest.")),
        ["FSNT"] = (new("夜班战友", "Night Shift Comrade"), new("同一款游戏，同一批人，每晚同一时间。", "Same game, same people, same time every night.")),
        ["FSDB"] = (new("赛季冲刺手", "Season Sprinter"), new("赛季一开就全力以赴，结算之后消失。", "All in when the season opens, gone after the final standings.")),
        ["FSDT"] = (new("老战友", "Old Comrade"), new("几年如一日，和老朋友玩同一款老游戏。", "Year after year, the same old game with the same old friends.")),
        ["FLNB"] = (new("通宵偏执狂", "All-Nighter Monomaniac"), new("一个人，一款游戏，不到通关不睡觉。", "One person, one game, no sleep until it is done.")),
        ["FLNT"] = (new("守夜人", "Night Keeper"), new("每晚一个人回到同一款游戏，像在值班。", "Returns alone to the same game every night, as if on duty.")),
        ["FLDB"] = (new("闭关修行者", "Secluded Grinder"), new("选定一款游戏闭关数日，出关时已是另一个人。", "Withdraws into one game for days and comes out changed.")),
        ["FLDT"] = (new("匠人", "Artisan"), new("一款游戏，一个人，每天一点，慢慢打磨。", "One game, alone, a little each day, polished slowly."))
    };

    public static IReadOnlyList<PlayerPersona> ByYear(InsightArchive archive)
    {
        var result = new List<PlayerPersona>();
        foreach (var group in archive.Sessions.GroupBy(session => int.Parse(session.Day[..4], System.Globalization.CultureInfo.InvariantCulture)).OrderBy(group => group.Key))
        {
            var sessions = group.ToList();
            var hours = InsightMath.Hours(sessions.Sum(session => session.Seconds));
            if (sessions.Count < MinimumSessions || hours < MinimumHours) continue;
            result.Add(Analyze(archive, group.Key, sessions, hours));
        }
        return result;
    }

    private static PlayerPersona Analyze(InsightArchive archive, int year, IReadOnlyList<InsightSession> sessions, double hours)
    {
        var profile = YearProfile.Build(archive, year, sessions);
        var explore = Axis("explore", Math.Log(Math.Max(profile.EffectiveGames, 1) / 5) / Math.Log(5), "E", "F", profile.EffectiveGames);
        var social = Axis("social", (profile.SocialShare - 0.25) / 0.25, "S", "L", profile.SocialShare);
        var night = Axis("night", (profile.NightShare - 0.3) / 0.25, "N", "D", profile.NightShare);
        var burst = Axis("burst", (profile.WeeklyVariation - 0.9) / 0.6, "B", "T", profile.WeeklyVariation);
        var axes = new[] { explore, social, night, burst };
        var code = string.Concat(axes.Select(axis => axis.Letter));
        var (name, summary) = Types[code];
        return new PlayerPersona(year, code, name, summary, axes, YearTitle.Build(year, profile), sessions.Count, hours);
    }

    private static PersonaAxis Axis(string id, double raw, string positive, string negative, double value)
    {
        var score = InsightMath.Clamp(raw, -1, 1);
        return new PersonaAxis(id, score, score >= 0 ? positive : negative, value);
    }
}

/// <summary>算人格和称号用到的一年数字。</summary>
internal sealed record YearProfile(
    double EffectiveGames,
    int DistinctGames,
    double TopGameShare,
    double SocialShare,
    double CoopShare,
    double NightShare,
    double MorningShare,
    double LunchShare,
    double WeekendShare,
    double WeeklyVariation,
    double IdleShare,
    double OldGameShare,
    double NewReleaseShare,
    int Completed,
    int Comebacks,
    int Acquired,
    int AcquiredAndStarted)
{
    internal static YearProfile Build(InsightArchive archive, int year, IReadOnlyList<InsightSession> sessions)
    {
        var options = archive.Options;
        var zone = options.TimeZone;
        double total = sessions.Sum(session => session.Seconds);
        double Share(Func<InsightSession, double> seconds) => total <= 0 ? 0 : sessions.Sum(seconds) / total;

        var perApp = sessions.GroupBy(session => session.AppId).Select(group => (AppId: group.Key, Seconds: (double)group.Sum(item => item.Seconds))).ToList();
        var entropy = perApp.Where(item => item.Seconds > 0).Sum(item => { var share = item.Seconds / total; return -share * Math.Log(share); });

        var weeks = sessions
            .GroupBy(session => options.WeekOf(InsightDays.Parse(session.Day)).DayNumber)
            .ToDictionary(group => group.Key, group => InsightMath.Hours(group.Sum(item => item.Seconds)));
        var weekly = new List<double>();
        for (var week = weeks.Keys.Min(); week <= weeks.Keys.Max(); week += 7) weekly.Add(weeks.GetValueOrDefault(week));
        var (mean, deviation) = InsightMath.MeanDeviation(weekly);

        double ShareOfRelease(Func<long, bool> matches)
        {
            double known = 0, hit = 0;
            foreach (var (appId, seconds) in perApp)
            {
                if (archive.FactsOf(appId)?.ReleaseDate is not { } release) continue;
                known += seconds;
                if (matches(release)) hit += seconds;
            }
            return known <= 0 ? 0 : hit / known;
        }
        var yearStart = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        const long Year = 365L * 86400;

        var coopShare = Share(session => (session.Tags & SessionTags.Coop) != 0 ? session.Seconds : 0);
        var onlineShare = perApp.Sum(item => SessionTagger.CanPlayTogether(archive.FactsOf(item.AppId)?.Tags) && archive.FactsOf(item.AppId)?.Tags.Count > 0 ? item.Seconds : 0) / Math.Max(total, 1);

        var yearText = year.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var played = sessions.Select(session => session.AppId).ToHashSet();
        var owned = archive.Owned.Select(game => game.AppId).ToHashSet();
        var acquired = archive.Acquisitions
            .Where(item => owned.Contains(item.AppId) && options.DayOf(item.AcquiredAt).StartsWith(yearText, StringComparison.Ordinal))
            .Select(item => item.AppId)
            .ToHashSet();

        return new YearProfile(
            EffectiveGames: Math.Exp(entropy),
            DistinctGames: perApp.Count,
            TopGameShare: perApp.Count == 0 ? 0 : perApp.Max(item => item.Seconds) / Math.Max(total, 1),
            // 好友记录只在应用运行且登录时才有，没有时用「玩的是不是联机游戏」近似，打六折
            SocialShare: Math.Max(coopShare, onlineShare * 0.6),
            CoopShare: coopShare,
            NightShare: Share(session => SessionTagger.WindowSeconds(session.Start, session.End, 22, 24, zone) + SessionTagger.WindowSeconds(session.Start, session.End, 0, 5, zone)),
            MorningShare: Share(session => SessionTagger.WindowSeconds(session.Start, session.End, 5, 9, zone)),
            LunchShare: Share(session => SessionTagger.WindowSeconds(session.Start, session.End, 12, 14, zone)),
            WeekendShare: Share(session => InsightDays.Parse(session.Day).DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? session.Seconds : 0),
            WeeklyVariation: mean <= 0 ? 0 : deviation / mean,
            IdleShare: total <= 0 ? 0 : sessions.Sum(session => (double)session.IdleSeconds) / total,
            OldGameShare: ShareOfRelease(release => yearStart - release >= 8 * Year),
            NewReleaseShare: ShareOfRelease(release => release >= yearStart - Year),
            Completed: CompletedIn(archive, yearText),
            Comebacks: sessions.Count(session => (session.Tags & SessionTags.Comeback) != 0),
            Acquired: acquired.Count,
            AcquiredAndStarted: acquired.Count(played.Contains));
    }

    private static int CompletedIn(InsightArchive archive, string yearText)
    {
        var lastUnlock = archive.Unlocks.GroupBy(unlock => unlock.AppId).ToDictionary(group => group.Key, group => group.Max(unlock => unlock.Day)!);
        return archive.Progress.Count(progress => progress.Total > 0
                                                  && progress.Unlocked >= progress.Total
                                                  && lastUnlock.TryGetValue(progress.AppId, out var day)
                                                  && day.StartsWith(yearText, StringComparison.Ordinal));
    }
}

/// <summary>年度称号：一个时段形容词加一个身份名词，例如「2026 深夜考古学家」。每个词都能说出理由。</summary>
internal static class YearTitle
{
    private sealed record Word(string Zh, string En, double Strength, Bilingual Reason);

    internal static PersonaTitle Build(int year, YearProfile profile)
    {
        var adjective = Adjectives(profile).OrderByDescending(word => word.Strength).First();
        var noun = Nouns(profile).OrderByDescending(word => word.Strength).First();
        var yearText = year.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var space = adjective.En.Length > 0 ? " " : "";
        return new PersonaTitle(
            new Bilingual($"{yearText} {adjective.Zh}{noun.Zh}", $"{yearText} {adjective.En}{space}{noun.En}"),
            [adjective.Reason, noun.Reason]);
    }

    private static IEnumerable<Word> Adjectives(YearProfile p)
    {
        if (p.NightShare >= 0.35) yield return new Word("深夜", "Midnight", p.NightShare / 0.35, Reason("全年 {share} 的时长在 22 点之后", "{share} of the year's playtime came after 10 pm", p.NightShare));
        if (p.MorningShare >= 0.15) yield return new Word("清晨", "Dawn", p.MorningShare / 0.15, Reason("全年 {share} 的时长在早上 5 点到 9 点", "{share} of the year's playtime came between 5 and 9 am", p.MorningShare));
        if (p.LunchShare >= 0.2) yield return new Word("午休", "Lunchtime", p.LunchShare / 0.2, Reason("全年 {share} 的时长在午休时间", "{share} of the year's playtime came over lunch", p.LunchShare));
        if (p.WeekendShare >= 0.55) yield return new Word("周末", "Weekend", p.WeekendShare / 0.55, Reason("全年 {share} 的时长在周末", "{share} of the year's playtime came on weekends", p.WeekendShare));
        if (p.WeekendShare <= 0.15) yield return new Word("工作日", "Weekday", 0.15 / Math.Max(p.WeekendShare, 0.05), Reason("全年只有 {share} 的时长在周末", "Only {share} of the year's playtime came on weekends", p.WeekendShare));
        yield return new Word("全天候", "All-Hours", 0.5, new Bilingual("没有特别集中的时段", "No particular time of day stands out"));
    }

    private static IEnumerable<Word> Nouns(YearProfile p)
    {
        if (p.OldGameShare >= 0.4) yield return new Word("考古学家", "Archaeologist", p.OldGameShare / 0.4, Reason("{share} 的时长给了发售超过八年的老游戏", "{share} of playtime went to games over eight years old", p.OldGameShare));
        if (p.NewReleaseShare >= 0.4) yield return new Word("尝鲜者", "Early Adopter", p.NewReleaseShare / 0.4, Reason("{share} 的时长给了当年的新游戏", "{share} of playtime went to new releases", p.NewReleaseShare));
        if (p.Completed >= 5) yield return new Word("完美主义者", "Perfectionist", p.Completed / 5.0, Count("全年全成就 {count} 款", "{count} games completed this year", p.Completed));
        if (p.Comebacks >= 5) yield return new Word("回头客", "Returner", p.Comebacks / 5.0, Count("全年回坑 {count} 次", "{count} comebacks this year", p.Comebacks));
        if (p.Acquired >= 15 && p.AcquiredAndStarted <= p.Acquired * 0.3) yield return new Word("囤积家", "Hoarder", p.Acquired / 15.0, Count("全年入库 {count} 款，大多没有启动", "{count} games added this year, most never started", p.Acquired));
        if (p.IdleShare >= 0.25) yield return new Word("挂机师傅", "Idle Master", p.IdleShare / 0.25, Reason("{share} 的时长处于挂机", "{share} of playtime was idle", p.IdleShare));
        if (p.CoopShare >= 0.3) yield return new Word("联队长", "Squad Leader", p.CoopShare / 0.3, Reason("{share} 的时长和好友一起", "{share} of playtime was with friends", p.CoopShare));
        if (p.TopGameShare >= 0.5) yield return new Word("专一主义者", "Devotee", p.TopGameShare / 0.5, Reason("{share} 的时长给了同一款游戏", "{share} of playtime went to a single game", p.TopGameShare));
        if (p.DistinctGames >= 40) yield return new Word("杂食动物", "Omnivore", p.DistinctGames / 40.0, Count("全年玩了 {count} 款游戏", "{count} different games this year", p.DistinctGames));
        yield return new Word("馆长", "Curator", 0.5, new Bilingual("这一年没有特别突出的习惯", "No habit stood out this year"));
    }

    private static Bilingual Reason(string zh, string en, double share) => Say.Fill(new Bilingual(zh, en), ("share", Say.Percent(share)));

    private static Bilingual Count(string zh, string en, int count) => Say.Fill(new Bilingual(zh, en), ("count", count));
}
