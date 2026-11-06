namespace SteamStat.Core.Insights;

/// <summary>排一期报纸。这里只做挑选和排版，数字都来自档案，文案都来自 <see cref="WeeklyCopy"/>。</summary>
public static class WeeklyComposer
{
    private const int MaxBriefs = 3;
    private const int MaxObservations = 3;
    private const int MaxObituaries = 3;
    private const int MaxAds = 3;
    private const int MaxFriendItems = 4;
    private const double MinimumObituaryHours = 1;

    private sealed record Candidate(double Score, WeeklyStory Story);

    /// <summary>期号：第一局游戏所在的周是第 1 期。补印的往期也能算出自己的期号。</summary>
    public static int IssueNumber(InsightArchive archive, string weekStart)
    {
        if (archive.Sessions.Count == 0) return 1;
        var first = archive.Options.WeekOf(archive.Options.LogicalDate(archive.Sessions[0].Start));
        return Math.Max(1, (InsightDays.Parse(weekStart).DayNumber - first.DayNumber) / 7 + 1);
    }

    public static WeeklyPayload Compose(InsightArchive archive, InsightPeriod period, IReadOnlyList<Insight> insights, bool backfilled = false)
    {
        var number = IssueNumber(archive, period.FromDay);
        var week = archive.SessionsIn(period).ToList();
        var numbers = Numbers(archive, period, week);
        var quiet = week.Count == 0;
        var candidates = quiet ? [] : Stories(archive, period, week, number);
        var ranked = candidates.OrderByDescending(candidate => candidate.Score).Select(candidate => candidate.Story).ToList();
        var headline = ranked.Count > 0 ? ranked[0] : QuietStory(number);

        return new WeeklyPayload
        {
            Kind = "weekly",
            Number = number,
            FromDay = period.FromDay,
            ToDay = period.ToDay,
            GeneratedAt = archive.Now,
            Backfilled = backfilled,
            Quiet = quiet,
            Headline = headline,
            Briefs = ranked.Skip(1).Where(story => story.AppId == null || story.AppId != headline.AppId).Take(MaxBriefs).ToList(),
            Observations = insights
                .Where(insight => insight.Id != "causal.comeback")
                .Take(MaxObservations)
                .Select(InsightCopy.Describe)
                .ToList(),
            Numbers = numbers,
            Schedule = quiet ? null : Schedule(archive, period, week),
            Friends = Friends(archive, period, week),
            Obituaries = Obituaries(archive, period, number),
            Market = WeeklyMarketBuilder.Build(archive, period),
            Forecast = WeeklyForecastBuilder.Build(archive, period),
            Classifieds = Classifieds(archive, number)
        };
    }

    private static WeeklyStory QuietStory(int number)
    {
        var template = WeeklyCopy.Pick(WeeklyCopy.Quiet, WeeklyCopy.Seed(number, "quiet"));
        return new WeeklyStory("quiet", template.Title, template.Body);
    }

    private static WeeklyNumbers Numbers(InsightArchive archive, InsightPeriod period, IReadOnlyList<InsightSession> week)
    {
        var previousFrom = InsightDays.Shift(period.FromDay, -7);
        var previousTo = InsightDays.Shift(period.FromDay, -1);
        var firstSeen = archive.Sessions.GroupBy(session => session.AppId).ToDictionary(group => group.Key, group => group.Min(session => session.Day)!);
        return new WeeklyNumbers(
            week.Sum(session => session.Seconds),
            week.Sum(session => session.Seconds - session.IdleSeconds),
            week.Count,
            week.Select(session => session.AppId).Distinct().Count(),
            week.Select(session => session.Day).Distinct().Count(),
            archive.Unlocks.Count(unlock => InPeriod(unlock.Day, period)),
            week.Select(session => session.AppId).Distinct().Count(appId => InPeriod(firstSeen[appId], period)),
            week.Count(session => (session.Tags & SessionTags.LateNight) != 0),
            archive.SessionsIn(previousFrom, previousTo).Sum(session => session.Seconds));
    }

    private static bool InPeriod(string day, InsightPeriod period)
        => string.CompareOrdinal(day, period.FromDay) >= 0 && string.CompareOrdinal(day, period.ToDay) <= 0;

    private static WeeklyStory Render(WeeklyCopy.Template[] templates, string id, int number, int? appId, params (string Key, object Value)[] values)
    {
        var template = WeeklyCopy.Pick(templates, WeeklyCopy.Seed(number, id));
        return new WeeklyStory(id, Say.Fill(template.Title, values), Say.Fill(template.Body, values), appId);
    }

    private static List<Candidate> Stories(InsightArchive archive, InsightPeriod period, IReadOnlyList<InsightSession> week, int number)
    {
        var list = new List<Candidate>();
        var perApp = week
            .GroupBy(session => session.AppId)
            .Select(group => (AppId: group.Key, group.First().Name, Seconds: group.Sum(item => item.Seconds)))
            .OrderByDescending(item => item.Seconds)
            .ThenBy(item => item.AppId)
            .ToList();
        var total = perApp.Sum(item => item.Seconds);
        var top = perApp[0];
        var topShare = total <= 0 ? 0 : (double)top.Seconds / total;
        var hours = InsightMath.Hours(total);

        // 全成就和成就集
        var lastUnlock = archive.Unlocks.GroupBy(unlock => unlock.AppId).ToDictionary(group => group.Key, group => group.Max(unlock => unlock.Day)!);
        var completedApps = new HashSet<int>();
        foreach (var progress in archive.Progress.Where(item => item.Total > 0 && item.Unlocked >= item.Total))
        {
            if (!lastUnlock.TryGetValue(progress.AppId, out var day) || !InPeriod(day, period)) continue;
            completedApps.Add(progress.AppId);
            list.Add(new Candidate(100 + progress.Total * 0.01, Render(WeeklyCopy.Completed, $"completed:{progress.AppId}", number, progress.AppId,
                ("game", Say.Game(progress.AppId, progress.Name)), ("count", progress.Total))));
        }
        foreach (var set in archive.SetCompletions.Where(item => !completedApps.Contains(item.AppId) && InPeriod(archive.Options.DayOf(item.CompletedAt), period)))
        {
            var name = archive.Progress.FirstOrDefault(item => item.AppId == set.AppId)?.Name ?? $"App {set.AppId}";
            list.Add(new Candidate(75, Render(WeeklyCopy.SetCompleted, $"set:{set.AppId}:{set.GroupId}", number, set.AppId,
                ("game", Say.Game(set.AppId, name)), ("set", set.Name))));
        }

        // 生涯最长一局
        var longest = week.MaxBy(session => session.Seconds)!;
        var previousBest = archive.Sessions.Where(session => string.CompareOrdinal(session.Day, period.FromDay) < 0).Select(session => session.Seconds).DefaultIfEmpty(0).Max();
        if (previousBest > 0 && longest.Seconds > previousBest && InsightMath.Hours(longest.Seconds) >= 3)
            list.Add(new Candidate(90, Render(WeeklyCopy.RecordSession, "record", number, longest.AppId,
                ("game", Say.Game(longest.AppId, longest.Name)), ("duration", Say.Seconds(longest.Seconds)), ("previous", Say.Seconds(previousBest)))));

        // 回坑
        foreach (var comeback in ComebackAnalysis.Find(archive, period.FromDay, period.ToDay).Take(2))
            // 有线索的回坑才算新闻，「动机不明」排在后面
            list.Add(new Candidate(80 + Math.Min(6, comeback.GapDays / 60.0) + (comeback.MainCause != null ? 3 : 0), Render(WeeklyCopy.Comeback, $"comeback:{comeback.AppId}", number, comeback.AppId,
                ("game", Say.Game(comeback.AppId, comeback.Name)), ("gap", comeback.GapDays), ("hours", Say.Seconds(comeback.SecondsAfter)), ("cause", CauseSentence(comeback.MainCause)))));

        // 新作
        var firstDay = archive.Sessions.GroupBy(session => session.AppId).ToDictionary(group => group.Key, group => group.Min(session => session.Day)!);
        foreach (var game in perApp.Where(game => InPeriod(firstDay[game.AppId], period) && InsightMath.Hours(game.Seconds) >= 5).Take(1))
            list.Add(new Candidate(70, Render(WeeklyCopy.NewObsession, "newGame", number, game.AppId,
                ("game", Say.Game(game.AppId, game.Name)), ("hours", Say.Seconds(game.Seconds)))));

        // 半年新高
        var history = Enumerable.Range(1, 26)
            .Select(index => InsightMath.Hours(archive.SessionsIn(InsightDays.Shift(period.FromDay, -7 * index), InsightDays.Shift(period.ToDay, -7 * index)).Sum(session => session.Seconds)))
            .ToList();
        if (history.Count(value => value > 0) >= 8 && hours >= 10 && hours > history.Max())
            list.Add(new Candidate(65, Render(WeeklyCopy.Marathon, "marathon", number, top.AppId,
                ("hours", Say.Hours(hours)), ("average", Say.Hours(history.Where(value => value > 0).Average())))));

        // 闪退
        var crash = archive.Crashes.Where(item => InPeriod(archive.Options.DayOf(item.Start), period)).MaxBy(item => item.Attempts);
        if (crash != null)
        {
            var name = week.FirstOrDefault(session => session.AppId == crash.AppId)?.Name
                       ?? archive.Sessions.FirstOrDefault(session => session.AppId == crash.AppId)?.Name
                       ?? $"App {crash.AppId}";
            list.Add(new Candidate(60, Render(WeeklyCopy.Crash, "crash", number, crash.AppId,
                ("game", Say.Game(crash.AppId, name)), ("attempts", crash.Attempts), ("note", crash.AfterUpdate ? WeeklyCopy.CrashAfterUpdate : WeeklyCopy.CrashNoUpdate))));
        }

        // 囤积
        var owned = archive.Owned.Select(game => game.AppId).ToHashSet();
        var bought = archive.Acquisitions
            .Where(item => owned.Contains(item.AppId) && InPeriod(archive.Options.DayOf(item.AcquiredAt), period))
            .Select(item => item.AppId)
            .ToHashSet();
        if (bought.Count >= 3)
            list.Add(new Candidate(55, Render(WeeklyCopy.Backlog, "backlog", number, null,
                ("bought", bought.Count), ("played", bought.Count(appId => perApp.Any(game => game.AppId == appId))))));

        // 一家独大
        if (topShare >= 0.6 && InsightMath.Hours(top.Seconds) >= 3)
            list.Add(new Candidate(50, Render(WeeklyCopy.Dominance, "dominance", number, top.AppId,
                ("game", Say.Game(top.AppId, top.Name)), ("hours", Say.Seconds(top.Seconds)), ("share", Say.Percent(topShare)), ("rest", Say.Percent(1 - topShare)))));

        // 熬夜
        var late = week.Where(session => (session.Tags & SessionTags.LateNight) != 0).ToList();
        if (late.Count >= 3)
            list.Add(new Candidate(45, Render(WeeklyCopy.LateNight, "lateNight", number, null,
                ("count", late.Count), ("latest", Say.Clock(LatestHour(archive, late) ?? 0)))));

        // 杂食
        if (perApp.Count >= 6)
            list.Add(new Candidate(40, Render(WeeklyCopy.Variety, "variety", number, null,
                ("games", perApp.Count), ("average", Say.Hours(hours / perApp.Count)))));

        // 什么都没发生
        list.Add(new Candidate(10, Render(WeeklyCopy.Steady, "steady", number, top.AppId,
            ("game", Say.Game(top.AppId, top.Name)), ("hours", Say.Seconds(top.Seconds)), ("total", Say.Hours(hours)))));
        return list;
    }

    private static Bilingual CauseSentence(ComebackCause? cause)
    {
        var template = WeeklyCopy.ComebackCauses[cause?.Kind ?? "unknown"];
        return Say.Fill(template, ("before", cause?.DaysBefore ?? 0), ("name", cause?.Name ?? new Bilingual("", "")));
    }

    /// <summary>熬夜局里最晚的结束时刻。5 点之后结束的算通宵，这里按 24 小时之后的时刻比较。</summary>
    private static double? LatestHour(InsightArchive archive, IEnumerable<InsightSession> sessions)
    {
        double? latest = null;
        foreach (var session in sessions)
        {
            var local = archive.Options.Local(session.End);
            var hour = local.Hour + local.Minute / 60.0;
            var adjusted = hour < 12 ? hour + 24 : hour;
            if (latest == null || adjusted > latest) latest = adjusted;
        }
        return latest;
    }

    private static WeeklySchedule Schedule(InsightArchive archive, InsightPeriod period, IReadOnlyList<InsightSession> week)
    {
        var options = archive.Options;
        var from = InsightDays.Parse(period.FromDay);
        var heat = Enumerable.Range(0, 7).Select(_ => new int[24]).ToArray();
        long weekend = 0, total = 0;
        foreach (var session in week)
        {
            total += session.Seconds;
            if (InsightDays.Parse(session.Day).DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) weekend += session.Seconds;
            var cursor = session.Start;
            while (cursor < session.End)
            {
                var local = options.Local(cursor);
                var hourEnd = cursor + (3600 - local.Minute * 60 - local.Second);
                var next = Math.Min(hourEnd, session.End);
                var row = options.LogicalDate(cursor).DayNumber - from.DayNumber;
                // 跨周的尾巴不进这张图
                if (row is >= 0 and < 7) heat[row][local.Hour] += (int)Math.Round((next - cursor) / 60.0);
                cursor = next;
            }
        }

        var peakHour = Enumerable.Range(0, 24).OrderByDescending(hour => heat.Sum(row => row[hour])).First();
        var late = week.Where(session => (session.Tags & SessionTags.LateNight) != 0).ToList();
        var latest = late.Count > 0 ? LatestHour(archive, late) : null;
        var weekendShare = total <= 0 ? 0 : (double)weekend / total;
        var parts = new List<Bilingual>
        {
            Say.Fill(WeeklyCopy.ScheduleBase,
                ("sessions", week.Count),
                ("late", late.Count),
                ("peak", PeakText(peakHour)),
                ("weekend", Say.Percent(weekendShare)))
        };
        if (latest != null) parts.Add(Say.Fill(WeeklyCopy.ScheduleLatest, ("latest", Say.Clock(latest.Value))));
        var drift = new ScheduleDriftDetector().Detect(archive, period).FirstOrDefault();
        if (drift != null)
        {
            var shift = drift.Facts["shiftHours"];
            parts.Add(Say.Fill(WeeklyCopy.ScheduleDrift,
                ("shift", Say.Number(Math.Abs(shift))),
                ("direction", shift > 0 ? new Bilingual("晚了", "later") : new Bilingual("早了", "earlier"))));
        }
        return new WeeklySchedule(Say.Join("", " ", parts), heat, late.Count, latest, Math.Round(weekendShare, 3));
    }

    private static Bilingual PeakText(int hour) => new($"{hour:00} 点前后", $"around {hour:00}:00");

    private static WeeklyFriends? Friends(InsightArchive archive, InsightPeriod period, IReadOnlyList<InsightSession> week)
    {
        var items = new List<Bilingual>();
        var together = new Dictionary<(string SteamId, int AppId), (long Seconds, string Friend, string Game)>();
        foreach (var session in week.Where(session => (session.Tags & SessionTags.Coop) != 0))
            foreach (var play in archive.Friends.PlaysOf(session.AppId))
            {
                var overlap = Math.Min(play.End, session.End) - Math.Max(play.Start, session.Start);
                if (overlap <= 0) continue;
                var key = (play.SteamId, session.AppId);
                together[key] = (together.GetValueOrDefault(key).Seconds + overlap, play.Name, session.Name);
            }
        foreach (var (key, value) in together.OrderByDescending(item => item.Value.Seconds).ThenBy(item => item.Key.SteamId, StringComparer.Ordinal).Take(2))
            items.Add(Say.Fill(WeeklyCopy.FriendCoop,
                ("friend", Say.Friend(key.SteamId, value.Friend)),
                ("game", Say.Game(key.AppId, value.Game)),
                ("hours", Say.Seconds(value.Seconds))));

        var changes = archive.Friends.Changes.Where(change => InPeriod(archive.Options.DayOf(change.Time), period)).ToList();
        var added = changes.Count(change => change.Kind == "added");
        var removed = changes.Count(change => change.Kind == "removed");
        if (added > 0) items.Add(Say.Fill(WeeklyCopy.FriendAdded, ("count", added)));
        if (removed > 0) items.Add(Say.Fill(WeeklyCopy.FriendRemoved, ("count", removed)));
        foreach (var change in changes.Where(change => change is { Kind: "renamed", To.Length: > 0 }).OrderByDescending(change => change.Time).Take(1))
            items.Add(Say.Fill(WeeklyCopy.FriendRenamed, ("friend", Say.Friend(change.SteamId, change.From ?? change.Name)), ("name", change.To!)));
        return items.Count == 0 ? null : new WeeklyFriends(items.Take(MaxFriendItems).ToList());
    }

    /// <summary>
    /// 讣告：本周跨过弃坑天数的游戏，或这周被手动标记弃坑的游戏。
    /// 手动标记的「在玩」「想玩」「搁置」「通关」都不进讣告，手动永远优先。
    /// </summary>
    private static IReadOnlyList<WeeklyObituary> Obituaries(InsightArchive archive, InsightPeriod period, int number)
    {
        var abandonDays = archive.Options.AbandonDays;
        var statuses = archive.Statuses.GroupBy(item => item.AppId).ToDictionary(group => group.Key, group => group.MaxBy(item => item.UpdatedAt)!);
        var result = new List<(double Hours, WeeklyObituary Item)>();
        var periodEnd = InsightDays.Parse(period.ToDay);
        foreach (var group in archive.Sessions.GroupBy(session => session.AppId))
        {
            var last = group.MaxBy(session => session.End)!;
            var hours = InsightMath.Hours(group.Sum(session => session.Seconds));
            if (hours < MinimumObituaryHours) continue;
            var status = statuses.GetValueOrDefault(group.Key);
            bool fell;
            if (status != null)
            {
                fell = status.Status == "abandoned" && InPeriod(archive.Options.DayOf(status.UpdatedAt), period);
            }
            else
            {
                var deathDay = archive.Options.LogicalDate(last.End).AddDays(abandonDays);
                fell = InPeriod(InsightDays.Format(deathDay), period);
            }
            if (!fell) continue;
            var idleDays = Math.Max(0, periodEnd.DayNumber - archive.Options.LogicalDate(last.End).DayNumber);
            var nightShare = group.Sum(session => (double)SessionTagger.NightSeconds(session.Start, session.End, archive.Options.TimeZone)) / group.Sum(session => (double)session.Seconds);
            var epitaph = nightShare >= 0.3 ? WeeklyCopy.ObituaryNight : WeeklyCopy.Pick(WeeklyCopy.Epitaphs, WeeklyCopy.Seed(number, $"epitaph:{group.Key}"));
            var text = Say.Join("", " ", [
                Say.Fill(WeeklyCopy.ObituaryBase, ("game", Say.Game(group.Key, last.Name)), ("hours", Say.Hours(hours)), ("days", idleDays)),
                epitaph
            ]);
            result.Add((hours, new WeeklyObituary(group.Key, last.Name, Math.Round(hours, 1), idleDays, text)));
        }
        return result.OrderByDescending(item => item.Hours).Take(MaxObituaries).Select(item => item.Item).ToList();
    }

    /// <summary>分类广告：积压游戏登广告求领养。入库最久的二十款里按期号轮换，不会每周都是同一批。</summary>
    private static IReadOnlyList<WeeklyAd> Classifieds(InsightArchive archive, int number)
    {
        var acquired = archive.Acquisitions.GroupBy(item => item.AppId).ToDictionary(group => group.Key, group => group.Min(item => item.AcquiredAt));
        var pool = BacklogClearanceDetector.Backlog(archive)
            .Where(game => acquired.ContainsKey(game.AppId))
            .OrderBy(game => acquired[game.AppId])
            .ThenBy(game => game.AppId)
            .Take(20)
            .ToList();
        if (pool.Count == 0) return [];
        var ads = new List<WeeklyAd>();
        var offset = (WeeklyCopy.Seed(number, "ads") & int.MaxValue) % pool.Count;
        for (var index = 0; index < Math.Min(MaxAds, pool.Count); index++)
        {
            var game = pool[(offset + index) % pool.Count];
            var days = (int)((archive.Now - acquired[game.AppId]) / 86400);
            var template = game.PlaytimeMinutes > 0
                ? WeeklyCopy.Pick(WeeklyCopy.AdsTried, WeeklyCopy.Seed(number, $"ad:{game.AppId}"))
                : WeeklyCopy.Pick(WeeklyCopy.AdsUnplayed, WeeklyCopy.Seed(number, $"ad:{game.AppId}"));
            var text = Say.Fill(template, ("game", Say.Game(game.AppId, game.Name)), ("days", days), ("minutes", game.PlaytimeMinutes));
            if (archive.FactsOf(game.AppId)?.PriceCents is { } cents and > 0)
                text = Say.Join("", " ", [text, Say.Fill(WeeklyCopy.AdPrice, ("price", (cents / 100.0).ToString("$0.00", System.Globalization.CultureInfo.InvariantCulture)))]);
            ads.Add(new WeeklyAd(game.AppId, game.Name, text));
        }
        return ads;
    }

    /// <summary>号外：一件事，一个头版。</summary>
    public static WeeklyPayload ComposeExtra(InsightArchive archive, WeeklyExtraEvent item)
    {
        var day = archive.Options.DayOf(item.Time);
        var number = IssueNumber(archive, InsightDays.Format(archive.Options.WeekOf(InsightDays.Parse(day))));
        var game = item.AppId is { } appId ? Say.Game(appId, item.Name ?? $"App {appId}") : string.Empty;
        var story = item.Kind switch
        {
            "completed" => Render(WeeklyCopy.Completed, item.Key, number, item.AppId, ("game", game), ("count", (int)item.Value)),
            "record" => Render(WeeklyCopy.RecordSession, item.Key, number, item.AppId, ("game", game), ("duration", Say.Hours(item.Value)), ("previous", Say.Hours(item.Previous))),
            "app" => Render(WeeklyCopy.ExtraApp, item.Key, number, item.AppId, ("game", game), ("hours", (int)item.Value)),
            _ => Render(WeeklyCopy.ExtraTotal, item.Key, number, item.AppId, ("hours", (int)item.Value), ("days", Say.Whole(item.Value / 24)))
        };
        var today = archive.SessionsIn(day, day).ToList();
        return new WeeklyPayload
        {
            Kind = "extra",
            Number = number,
            FromDay = day,
            ToDay = day,
            GeneratedAt = archive.Now,
            Headline = story,
            Numbers = new WeeklyNumbers(
                today.Sum(session => session.Seconds),
                today.Sum(session => session.Seconds - session.IdleSeconds),
                today.Count,
                today.Select(session => session.AppId).Distinct().Count(),
                today.Count > 0 ? 1 : 0,
                archive.Unlocks.Count(unlock => unlock.Day == day),
                0,
                today.Count(session => (session.Tags & SessionTags.LateNight) != 0),
                0)
        };
    }
}

/// <summary>
/// 号外的触发条件：全成就、破纪录、累计时长里程碑。
/// 只看 since 之后发生的事，第一次运行时用当前时间做基线，不把历史翻出来出一堆号外。
/// </summary>
public static class WeeklyExtras
{
    public const double MinimumRecordHours = 4;
    public static readonly int[] TotalMilestones = [100, 500, 1000, 2000, 5000, 10000, 20000];
    public static readonly int[] AppMilestones = [100, 500, 1000, 2000, 5000];

    public static IReadOnlyList<WeeklyExtraEvent> Detect(InsightArchive archive, long since)
    {
        var events = new List<WeeklyExtraEvent>();

        var lastUnlock = archive.Unlocks.GroupBy(unlock => unlock.AppId).ToDictionary(group => group.Key, group => group.Max(unlock => unlock.Time));
        foreach (var progress in archive.Progress.Where(item => item.Total > 0 && item.Unlocked >= item.Total))
        {
            if (lastUnlock.GetValueOrDefault(progress.AppId) > since)
                events.Add(new WeeklyExtraEvent($"completed:{progress.AppId}", "completed", lastUnlock[progress.AppId], progress.AppId, progress.Name, progress.Total));
        }

        var before = archive.Sessions.Where(session => session.End <= since).ToList();
        var after = archive.Sessions.Where(session => session.End > since).ToList();
        if (after.Count == 0) return events;

        var previousBest = before.Select(session => session.Seconds).DefaultIfEmpty(0).Max();
        var best = after.MaxBy(session => session.Seconds)!;
        if (previousBest > 0 && best.Seconds > previousBest && InsightMath.Hours(best.Seconds) >= MinimumRecordHours)
            events.Add(new WeeklyExtraEvent($"record:{best.Id}", "record", best.End, best.AppId, best.Name, InsightMath.Hours(best.Seconds), InsightMath.Hours(previousBest)));

        var totalBefore = InsightMath.Hours(before.Sum(session => session.Seconds));
        var totalNow = InsightMath.Hours(archive.Sessions.Sum(session => session.Seconds));
        foreach (var hours in TotalMilestones.Where(hours => totalBefore < hours && totalNow >= hours))
            events.Add(new WeeklyExtraEvent($"total:{hours}", "total", after[^1].End, null, null, hours));

        foreach (var group in archive.Sessions.GroupBy(session => session.AppId))
        {
            var was = InsightMath.Hours(group.Where(session => session.End <= since).Sum(session => session.Seconds));
            var now = InsightMath.Hours(group.Sum(session => session.Seconds));
            foreach (var hours in AppMilestones.Where(hours => was < hours && now >= hours))
                events.Add(new WeeklyExtraEvent($"app:{group.Key}:{hours}", "app", group.Max(session => session.End), group.Key, group.First().Name, hours));
        }
        return events.OrderBy(item => item.Time).ToList();
    }
}
