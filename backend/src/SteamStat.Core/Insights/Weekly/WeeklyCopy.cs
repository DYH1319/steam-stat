namespace SteamStat.Core.Insights;

/// <summary>
/// 周刊文案。语气只有一种：冷幽默编辑，一本正经地讲荒诞的事。
/// 本报只把用户称作「当事人」，不说教、不提醒、不评价玩得多还是少。
/// 同一类故事有几种写法，按期号挑选，避免每周都读到同一句话。
/// </summary>
internal static class WeeklyCopy
{
    internal sealed record Template(Bilingual Title, Bilingual Body);

    private static Template T(string titleZh, string bodyZh, string titleEn, string bodyEn)
        => new(new Bilingual(titleZh, titleEn), new Bilingual(bodyZh, bodyEn));

    internal static readonly Template[] Dominance =
    [
        T("《{game}》占据本周 {share} 的游戏时间，当事人称局势稳定",
          "过去七天，当事人向《{game}》投入 {hours}。本报就此联系其待办事项，对方表示已于周二失去联系。",
          "{game} takes {share} of the week's playtime; subject calls the situation stable",
          "The subject committed {hours} to {game} over the past seven days. Reached for comment, the subject's to-do list said contact was lost on Tuesday."),
        T("《{game}》独揽本周 {hours}，其余游戏发表联合声明",
          "声明全文如下：「我们还在。」截至发稿，当事人未予回应。",
          "{game} claims {hours} of the week; other games issue joint statement",
          "The statement reads in full: \"We are still here.\" The subject had not responded at press time."),
        T("本周时间分配方案出炉：《{game}》{share}，其他 {rest}",
          "分析人士认为这一方案缺乏多样性。当事人回应称「下周一定换一个」，该表述已被本报收入常用词库。",
          "This week's time budget: {game} {share}, everything else {rest}",
          "Analysts called the allocation undiversified. The subject replied, \"Something else next week, definitely,\" a phrase this paper now keeps on file.")
    ];

    internal static readonly Template[] RecordSession =
    [
        T("单局 {duration}：《{game}》刷新当事人生涯最长纪录",
          "上一纪录为 {previous}，保持至本周。当事人起身时，窗外已经换了一批路人。",
          "{duration} in one sitting: {game} sets a new career record",
          "The previous record of {previous} stood until this week. By the time the subject stood up, the view outside had changed shifts."),
        T("「再玩一局」实测 {duration}，创档案新高",
          "这一局发生在《{game}》。本报认为纪录生来就是要被打破的，但不必总由同一把椅子承担。",
          "\"One more round\" measured at {duration}, an archive high",
          "The round took place in {game}. Records exist to be broken, this paper notes, though not always by the same chair.")
    ];

    internal static readonly Template[] Comeback =
    [
        T("时隔 {gap} 天，当事人重返《{game}》",
          "现场没有欢迎仪式。回归后累计 {hours}。{cause}",
          "After {gap} days, the subject returns to {game}",
          "There was no welcoming ceremony. Time logged since the return: {hours}. {cause}"),
        T("《{game}》在沉寂 {gap} 天后被重新启动，存档表示并不意外",
          "知情人士透露，当事人进入游戏后花了一段时间回忆按键。回归后累计 {hours}。{cause}",
          "{game} restarted after {gap} silent days; save file says it saw this coming",
          "Sources say the subject spent the opening minutes trying to remember the controls. Time logged since the return: {hours}. {cause}")
    ];

    internal static readonly IReadOnlyDictionary<string, Bilingual> ComebackCauses = new Dictionary<string, Bilingual>
    {
        ["update"] = new("本报注意到，{before} 天前该游戏刚刚更新，两者的关系有待调查。", "The game had been updated {before} days earlier, a connection this paper is still looking into."),
        ["dlc"] = new("当事人在 {before} 天前购入了它的 DLC，动机已经比较明显。", "The subject had bought its DLC {before} days earlier, which narrows down the motive."),
        ["friend"] = new("此前 {name} 正在玩这款游戏，从众心理不能排除。", "{name} had been playing it, so peer pressure has not been ruled out."),
        ["holiday"] = new("时值{name}，当事人称这是休息。", "It was {name}. The subject calls this rest."),
        ["sale"] = new("时值{name}，当事人否认两者有关。", "It was {name}. The subject denies any connection."),
        ["unknown"] = new("动机不明。", "The motive remains unknown.")
    };

    internal static readonly Template[] NewObsession =
    [
        T("新作《{game}》首周录得 {hours}，当事人称「先试试」",
          "本报查阅档案后确认，当事人上次也是这么说的。",
          "New arrival {game} logs {hours} in its first week; subject says just trying it out",
          "A check of the archive confirms the subject said the same thing last time."),
        T("《{game}》入库即上岗，一周 {hours}",
          "积压库中的前辈们对这次破格录用表示不解。",
          "{game} starts work immediately, {hours} in week one",
          "Senior members of the backlog expressed confusion at the fast-track appointment.")
    ];

    internal static readonly Template[] Marathon =
    [
        T("本周总时长 {hours}，创近半年新高",
          "此前半年的周均值为 {average}。本报无法判断这是一次假期还是一次事故，但数字是真实的。",
          "{hours} this week, the highest in six months",
          "The six-month weekly average was {average}. This paper cannot tell a holiday from an incident, but the figures are genuine.")
    ];

    internal static readonly Template[] Backlog =
    [
        T("本周新入库 {bought} 款游戏，启动 {played} 款",
          "库存管理部门表示情况可控。上次它也是这么说的。",
          "{bought} games added this week, {played} launched",
          "The inventory department says the situation is under control. It said so last time as well.")
    ];

    internal static readonly Template[] LateNight =
    [
        T("本周 {count} 局游戏在凌晨进行，最晚收工于 {latest}",
          "当事人的生物钟已递交辞呈，目前由咖啡代管。",
          "{count} sessions ran past midnight this week, the latest ending at {latest}",
          "The subject's body clock has handed in its resignation. Coffee is covering its duties for now.")
    ];

    internal static readonly Template[] Variety =
    [
        T("本周开启 {games} 款游戏，平均每款 {average}",
          "学界称这种行为为「探索」，当事人称之为「不知道玩什么」。",
          "{games} games opened this week, {average} each on average",
          "Academics call this exploration. The subject calls it not knowing what to play.")
    ];

    internal static readonly Template[] Crash =
    [
        T("《{game}》连续闪退 {attempts} 次，当事人坚持尝试",
          "这是一种罕见的信任。{note}",
          "{game} crashes {attempts} times in a row; subject keeps trying",
          "This is a rare kind of trust. {note}")
    ];

    internal static readonly Bilingual CrashAfterUpdate = new("版本记录显示，该游戏此前刚刚更新。", "Version records show the game had just been updated.");
    internal static readonly Bilingual CrashNoUpdate = new("版本记录未见异常，问题可能出在宇宙。", "Version records show nothing unusual. The problem may lie with the universe.");

    internal static readonly Template[] Completed =
    [
        T("《{game}》全成就达成，{count} 项无一遗漏",
          "本报向当事人表示祝贺。按照惯例，这款游戏将于近期进入一段体面的沉默。",
          "{game} completed, all {count} achievements accounted for",
          "This paper extends its congratulations. By custom, the game will now enter a dignified silence."),
        T("当事人宣布《{game}》{count} 项成就已全部结清",
          "债权人表示满意。当事人已开始在库里寻找下一笔债务。",
          "Subject declares all {count} achievements in {game} settled",
          "Creditors are satisfied. The subject has begun browsing the library for the next debt.")
    ];

    internal static readonly Template[] SetCompleted =
    [
        T("《{game}》成就集「{set}」完成",
          "这是一次局部胜利。本报不打算追问其余部分。",
          "Achievement set \"{set}\" completed in {game}",
          "A partial victory. This paper will not be asking about the rest.")
    ];

    internal static readonly Template[] Steady =
    [
        T("本周无重大事件，《{game}》以 {hours} 继续领先",
          "全周累计 {total}，各项指标平稳。平稳本身也是新闻，只是不太好卖。",
          "No major events this week; {game} leads with {hours}",
          "The week totaled {total}, with all indicators stable. Stability is news too, though it sells poorly."),
        T("《{game}》{hours}，全周 {total}，一切照常",
          "本报记者在现场守候七天，未发现异常。记者已申请调岗。",
          "{game} {hours}, week total {total}, business as usual",
          "Our correspondent stood watch for seven days and observed nothing unusual. The correspondent has requested a transfer.")
    ];

    internal static readonly Template[] Quiet =
    [
        T("本周无事发生",
          "过去七天，本报未观测到任何游戏活动。编辑部已确认这不是设备故障。所有存档状态良好，正在等待。",
          "Nothing happened this week",
          "This paper observed no gaming activity over the past seven days. The editors have confirmed this is not an equipment fault. All save files are in good health and waiting.")
    ];

    internal static readonly Template[] ExtraTotal =
    [
        T("号外：累计时长突破 {hours} 小时",
          "这相当于不眠不休 {days} 天。本报不建议这样做，但尊重这个数字。",
          "Extra: total playtime passes {hours} hours",
          "That equals {days} days without sleep. This paper does not recommend it, but respects the figure.")
    ];

    internal static readonly Template[] ExtraApp =
    [
        T("号外：《{game}》累计突破 {hours} 小时",
          "当事人与这款游戏共处的时间，已经超过了大多数实习期。",
          "Extra: {game} passes {hours} hours",
          "The subject has now spent longer with this game than most internships last.")
    ];

    internal static readonly Bilingual ScheduleBase = new(
        "本周 {sessions} 局游戏中，{late} 局进入凌晨。{peak}最为活跃，{weekend} 的时长发生在周末。",
        "Of {sessions} sessions this week, {late} ran past midnight. Activity peaked {peak}, and {weekend} of playtime fell on the weekend.");

    internal static readonly Bilingual ScheduleLatest = new("最晚收工时间为 {latest}。", "The latest finish was {latest}.");

    internal static readonly Bilingual ScheduleDrift = new("开局时间比此前四周平均{direction} {shift} 小时。", "Sessions began {shift} hours {direction} than the previous four weeks.");

    internal static readonly Bilingual FriendCoop = new("{friend} 与当事人在《{game}》共处 {hours}，双方称会谈坦诚而漫长。", "{friend} and the subject spent {hours} together in {game}. Both sides described the talks as frank and lengthy.");
    internal static readonly Bilingual FriendAdded = new("好友名单新增 {count} 人，欢迎仪式从简。", "{count} new names joined the friends list. The welcome was kept simple.");
    internal static readonly Bilingual FriendRemoved = new("好友名单减少 {count} 人，本报不作进一步报道。", "{count} names left the friends list. This paper will not report further.");
    internal static readonly Bilingual FriendRenamed = new("{friend} 更名为「{name}」，原因不明，本报已更新通讯录。", "{friend} now goes by \"{name}\". No reason was given, and this paper has updated its records.");

    internal static readonly Bilingual ObituaryBase = new("《{game}》，享年 {hours}。最后一次启动在 {days} 天前。", "{game}, aged {hours}. Last launched {days} days ago.");

    internal static readonly Bilingual[] Epitaphs =
    [
        new("它走得很安静，甚至没有弹出更新。", "It went quietly, without so much as an update prompt."),
        new("存档尚在，家属谢绝探视。", "The save file survives. The family asks for privacy."),
        new("生前从未抱怨过等待。", "It never once complained about the wait."),
        new("云存档已先行一步。", "Its cloud save has gone on ahead."),
        new("当事人未出席，也未发表悼词。", "The subject did not attend and offered no eulogy.")
    ];

    internal static readonly Bilingual ObituaryNight = new("生前以凌晨活动著称。", "It was known for its late-night activity.");

    internal static readonly Bilingual MarketBase = new(
        "综合指数收于 {index} 点，周{direction} {change}。",
        "The composite closed at {index}, {direction} {change} on the week.");
    internal static readonly Bilingual MarketFlat = new("综合指数收于 {index} 点。", "The composite closed at {index}.");
    internal static readonly Bilingual MarketLeader = new("领涨《{game}》，放量 {hours}。", "{game} led the gains on {hours} of volume.");
    internal static readonly Bilingual MarketLaggard = new("《{game}》领跌，分析师建议：去玩一下。", "{game} led the decline. Analysts recommend playing it.");

    internal static readonly Bilingual ForecastSummary = new("下周最可能游玩的时段是{day}{part}，概率 {probability}。", "Next week's best chance of play is {day} {part}, at {probability}.");
    internal static readonly Bilingual ForecastFriend = new("{friend} 最可能在{day}{part}上线，近八周有 {share} 的时候如此。", "{friend} is most likely online {day} {part}, as on {share} of the past eight weeks.");

    internal static readonly IReadOnlyDictionary<string, Bilingual> Skies = new Dictionary<string, Bilingual>
    {
        ["clear"] = new("晴，宜开机", "Clear, good for launching"),
        ["cloudy"] = new("多云转游戏", "Cloudy, turning to games"),
        ["showers"] = new("零星游戏", "Scattered gaming"),
        ["calm"] = new("无明显活动", "No significant activity")
    };

    internal static readonly IReadOnlyDictionary<string, Bilingual> Parts = new Dictionary<string, Bilingual>
    {
        ["morning"] = new("上午", "morning"),
        ["afternoon"] = new("下午", "afternoon"),
        ["evening"] = new("晚间", "evening"),
        ["night"] = new("凌晨", "late night")
    };

    internal static readonly Bilingual[] AdsUnplayed =
    [
        new("【求领养】《{game}》，入库 {days} 天，从未启动。性情安静，不吵不闹。有意者请双击。", "FOR ADOPTION: {game}. In the library {days} days, never launched. Quiet temperament. Interested parties, double-click."),
        new("【诚寻玩家】《{game}》，全新未拆。库主称「等有空」已 {days} 天。", "PLAYER WANTED: {game}, as new. Owner has been saying \"when I have time\" for {days} days."),
        new("【招租】《{game}》，空置 {days} 天，采光良好，可随时入住。", "TO LET: {game}. Vacant {days} days, well lit, immediate occupancy.")
    ];

    internal static readonly Bilingual[] AdsTried =
    [
        new("【转让时间】《{game}》，累计使用 {minutes} 分钟，近乎全新。转让原因：当时只是看看。", "TIME FOR SALE: {game}, {minutes} minutes on the clock, nearly new. Reason for sale: was only looking."),
        new("【寻人】《{game}》的当事人，{minutes} 分钟后再无音讯。如有线索请启动游戏。", "MISSING PERSON: the subject of {game}, last seen after {minutes} minutes. Anyone with information, please launch the game.")
    ];

    internal static readonly Bilingual AdPrice = new("标价 {price}。", "Listed at {price}.");

    internal static T Pick<T>(IReadOnlyList<T> options, int seed) => options[(seed & int.MaxValue) % options.Count];

    /// <summary>字符串的稳定哈希。string.GetHashCode 每次进程启动都不同，同一期要选到同一句话。</summary>
    internal static int Seed(int number, string key)
    {
        var hash = 2166136261u;
        foreach (var character in key)
            hash = (hash ^ character) * 16777619u;
        return (int)((hash ^ (uint)number) * 2654435761u >> 8);
    }
}
