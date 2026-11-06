namespace SteamStat.Core.Insights;

/// <summary>把一条洞察写成一句话。周刊的「本报观察」和洞察页共用，两种语言都写好。</summary>
public static class InsightCopy
{
    public static Bilingual Describe(Insight insight)
    {
        var facts = insight.Facts;
        double F(string key) => facts.GetValueOrDefault(key);
        var game = insight.AppId is { } appId ? Say.Game(appId, insight.AppName ?? $"App {appId}") : string.Empty;
        return insight.Id switch
        {
            "behavior.idleRate" => Say.Fill(
                new Bilingual("这段时间 {rate} 的游戏时长处于挂机状态，共 {idle}。当事人本人不在场。", "{rate} of this period's playtime was idle, {idle} in all. The subject was not present."),
                ("rate", Say.Percent(F("rate"))), ("idle", Say.Hours(F("idleSeconds") / 3600))),
            "behavior.scheduleDrift" => Say.Fill(
                F("shiftHours") > 0
                    ? new Bilingual("开局时间比之前晚了 {shift} 小时，目前平均在 {clock}。", "Sessions now start {shift} hours later than before, around {clock}.")
                    : new Bilingual("开局时间比之前早了 {shift} 小时，目前平均在 {clock}。", "Sessions now start {shift} hours earlier than before, around {clock}."),
                ("shift", Say.Number(Math.Abs(F("shiftHours")))), ("clock", Say.Clock(F("currentHour")))),
            "behavior.droppedGenre" => Say.Fill(
                new Bilingual("「{tag}」类游戏有 {rate} 玩了不到两小时就放下了（{games} 款中的 {dropped} 款），整体水平是 {overall}。", "{rate} of your {tag} games were dropped within two hours ({dropped} of {games}). Your overall rate is {overall}."),
                ("tag", insight.Labels.GetValueOrDefault("tag", "")), ("rate", Say.Percent(F("rate"))), ("games", Say.Whole(F("games"))), ("dropped", Say.Whole(F("dropped"))), ("overall", Say.Percent(F("overallRate")))),
            "behavior.playStyle" => Say.Fill(
                insight.Labels.GetValueOrDefault("style") == "binge"
                    ? new Bilingual("过去一年，{share} 的时长集中在每款游戏最忙的七天里。这是爆肝型。", "Over the past year, {share} of your playtime fell in each game's busiest seven days. That is a binge player.")
                    : new Bilingual("过去一年，只有 {share} 的时长集中在每款游戏最忙的七天里。这是细水长流型。", "Over the past year, only {share} of your playtime fell in each game's busiest seven days. That is a slow burner."),
                ("share", Say.Percent(F("concentration")))),
            "causal.comeback" => Comeback(insight, game),
            "forecast.backlogClearance" => Say.Fill(
                new Bilingual("积压库共 {backlog} 款。按过去一年开新坑的速度（每年 {perYear} 款），清完需要 {years} 年。", "The backlog stands at {backlog} games. At last year's pace of {perYear} new starts, clearing it takes {years} years."),
                ("backlog", Say.Whole(F("backlog"))), ("perYear", Say.Whole(F("perYear"))), ("years", Say.Number(F("years")))),
            "forecast.completionDate" => Say.Fill(
                new Bilingual("《{game}》还差 {remaining} 个成就。按最近三个月的节奏，约 {days} 天后完成。", "{game} has {remaining} achievements left. At the pace of the last three months, that is about {days} days away."),
                ("game", game), ("remaining", Say.Whole(F("remaining"))), ("days", Say.Whole(F("days")))),
            "forecast.abandonRisk" => Say.Fill(
                new Bilingual("《{game}》已经 {idle} 天没碰了。历史上走到这一步的游戏，{probability} 再也没回去（{cases} 次）。", "{game} has gone {idle} days untouched. Of the {cases} times this happened before, {probability} never came back."),
                ("game", game), ("idle", Say.Whole(F("idleDays"))), ("probability", Say.Percent(F("probability"))), ("cases", Say.Whole(F("cases")))),
            "anomaly.playtimeSpike" => Say.Fill(
                new Bilingual("这段时间玩了 {hours}，同期平均 {mean}，明显偏高。", "{hours} this period against a usual {mean}, well above normal."),
                ("hours", Say.Hours(F("hours"))), ("mean", Say.Hours(F("mean")))),
            "anomaly.playtimeDrop" => Say.Fill(
                new Bilingual("这段时间只玩了 {hours}，同期平均 {mean}，明显偏低。", "Only {hours} this period against a usual {mean}, well below normal."),
                ("hours", Say.Hours(F("hours"))), ("mean", Say.Hours(F("mean")))),
            "anomaly.officialDiff" => Say.Fill(
                new Bilingual("{count} 款游戏的本地记录和 Steam 官方时长对不上，占 {share}。", "{count} games disagree with the official Steam playtime, {share} of those checked."),
                ("count", Say.Whole(F("untracked") + F("overcounted"))), ("share", Say.Percent(F("share")))),
            "anomaly.recordGap" => Say.Fill(
                new Bilingual("有 {days} 天官方时长增加了 {hours}，本地却没有记录，应用当时可能没在运行。", "Over {days} days the official playtime grew by {hours} with nothing recorded here. The app may not have been running."),
                ("days", Say.Whole(F("days"))), ("hours", Say.Hours(F("seconds") / 3600))),
            _ => new Bilingual(insight.Id, insight.Id)
        };
    }

    private static Bilingual Comeback(Insight insight, string game)
    {
        var gap = Say.Whole(insight.Facts.GetValueOrDefault("gapDays"));
        var before = Say.Whole(insight.Facts.GetValueOrDefault("causeDaysBefore"));
        var name = new Bilingual(insight.Labels.GetValueOrDefault("causeZh", ""), insight.Labels.GetValueOrDefault("causeEn", ""));
        var template = insight.Labels.GetValueOrDefault("cause") switch
        {
            "update" => new Bilingual("《{game}》时隔 {gap} 天回归，{before} 天前它刚更新过。", "{game} returned after {gap} days. It had been updated {before} days earlier."),
            "dlc" => new Bilingual("《{game}》时隔 {gap} 天回归，{before} 天前你买了它的 DLC。", "{game} returned after {gap} days. You had bought its DLC {before} days earlier."),
            "friend" => new Bilingual("《{game}》时隔 {gap} 天回归，此前 {name} 在玩它。", "{game} returned after {gap} days. {name} had been playing it."),
            "holiday" => new Bilingual("《{game}》时隔 {gap} 天回归，时值{name}。", "{game} returned after {gap} days, during {name}."),
            "sale" => new Bilingual("《{game}》时隔 {gap} 天回归，时值{name}。", "{game} returned after {gap} days, during {name}."),
            _ => new Bilingual("《{game}》时隔 {gap} 天回归。", "{game} returned after {gap} days.")
        };
        return Say.Fill(template, ("game", game), ("gap", gap), ("before", before), ("name", name));
    }
}
