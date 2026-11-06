using SteamStat.Contracts.Ipc;

namespace ElectronNet.Features.Semantic;

/// <summary>
/// 视图与列的说明：同时是 SQL 控制台的文档和 AI 的工具说明。
/// 有测试校验每个视图的每一列都有说明，改视图时要一起改这里。
/// </summary>
internal static class SemanticCatalog
{
    private static BilingualTextDto T(string zh, string en) => new(zh, en);

    internal static readonly IReadOnlyDictionary<string, BilingualTextDto> ViewDescriptions = new Dictionary<string, BilingualTextDto>
    {
        ["v_session"] = T("清洗后的游戏会话：已合并相邻会话、过滤过短会话，已剔除隐藏和排除的游戏。一行是一局。", "Cleaned game sessions: adjacent records merged, short ones dropped, hidden and excluded games removed. One row per session."),
        ["v_session_day"] = T("会话按逻辑日拆开的片段，跨越分界点的会话有多行。按天统计时用它，否则跨天的时长会算错。", "Sessions split by logical day. A session crossing the boundary has several rows. Use this for per-day totals."),
        ["v_daily_playtime"] = T("每账号每个逻辑日的时长汇总，不含游戏明细。", "Playtime per account per logical day, without per-game detail."),
        ["v_app_playtime"] = T("每账号每款游戏的累计时长。只含本应用记录到的会话，不含安装前的历史。", "Total playtime per account and game. Covers sessions recorded by this app, not the history before it was installed."),
        ["v_app"] = T("游戏元数据。", "Game metadata."),
        ["v_user"] = T("本机账号，不含昵称和登录名。", "Local accounts, without persona or login names."),
        ["v_user_name"] = T("本机账号的昵称。", "Persona names of local accounts."),
        ["v_official_playtime"] = T("Steam 官方时长的观测记录，每次变化一行。", "Observed official Steam playtime, one row per change."),
        ["v_achievement_unlock"] = T("已解锁的成就。", "Unlocked achievements."),
        ["v_achievement_game"] = T("每款游戏的成就进度。", "Achievement progress per game."),
        ["v_library_status"] = T("用户标记的游戏状态和评分。", "Game status and rating set by the user."),
        ["v_library_acquisition"] = T("游戏入库时间。", "When games entered the library."),
        ["v_screenshot"] = T("Steam 截图索引，不含文件路径。", "Steam screenshot index, without file paths."),
        ["v_profile_change"] = T("Steam 资料的变化历史。", "History of Steam profile changes."),
        ["v_session_note"] = T("用户为会话写的备注和标签。", "Notes and tags written by the user for sessions."),
        ["v_friend_event"] = T("手动追踪好友的状态变化和好友列表增删。", "Status changes of manually tracked friends, plus friend list additions and removals."),
        ["v_rich_presence"] = T("游戏内状态记录，含自己和手动追踪的好友，保留原始键值。", "In-game status records for yourself and manually tracked friends, with the raw key-value pairs.")
    };

    /// <summary>同名列含义相同，按列名说明；个别视图含义不同时用 视图.列 覆盖。</summary>
    internal static readonly IReadOnlyDictionary<string, BilingualTextDto> ColumnDescriptions = new Dictionary<string, BilingualTextDto>
    {
        ["steam_id"] = T("账号的 SteamID64", "SteamID64 of the account"),
        ["app_id"] = T("Steam 应用 ID", "Steam app ID"),
        ["app_name"] = T("游戏名称", "Game name"),
        ["name"] = T("名称", "Name"),
        ["logical_day"] = T("逻辑日（YYYY-MM-DD），分界点之前的时间算前一天", "Logical day (YYYY-MM-DD). Time before the day boundary counts as the previous day"),
        ["session_id"] = T("会话 ID（合并时取最早一条记录的 ID）", "Session ID (the earliest record's ID when merged)"),
        ["start_time"] = T("开始时间（Unix 秒）", "Start time (Unix seconds)"),
        ["end_time"] = T("结束时间（Unix 秒）", "End time (Unix seconds)"),
        ["duration_seconds"] = T("时长（秒），含挂机", "Duration in seconds, including idle time"),
        ["idle_seconds"] = T("挂机时长（秒）", "Idle time in seconds"),
        ["effective_seconds"] = T("有效时长（秒）= 时长 − 挂机", "Effective seconds = duration − idle"),
        ["foreground_seconds"] = T("游戏窗口在前台的时长（秒），未采集时为空", "Seconds with the game in the foreground, empty when not collected"),
        ["record_count"] = T("合并进这局的原始记录数", "Raw records merged into this session"),
        ["source"] = T("数据来源", "Data source"),
        ["device_id"] = T("采集设备 ID", "ID of the recording device"),
        ["recovered_records"] = T("其中结束时间由心跳推算的记录数", "Records whose end time was inferred from a heartbeat"),
        ["seconds"] = T("时长（秒）", "Seconds"),
        ["session_count"] = T("会话数", "Number of sessions"),
        ["app_count"] = T("游戏数", "Number of games"),
        ["longest_session_seconds"] = T("最长一局（秒）", "Longest session in seconds"),
        ["first_played"] = T("首次游玩（Unix 秒）", "First played (Unix seconds)"),
        ["last_played"] = T("最近游玩（Unix 秒）", "Last played (Unix seconds)"),
        ["type"] = T("应用类型", "App type"),
        ["developer"] = T("开发商", "Developer"),
        ["publisher"] = T("发行商", "Publisher"),
        ["release_date"] = T("发行日期（Unix 秒）", "Release date (Unix seconds)"),
        ["is_free"] = T("是否免费", "Whether the game is free"),
        ["installed"] = T("是否已安装", "Whether the game is installed"),
        ["level"] = T("Steam 等级", "Steam level"),
        ["persona_name"] = T("昵称", "Persona name"),
        ["playtime_minutes"] = T("官方累计时长（分钟）", "Official total playtime in minutes"),
        ["first_observed_at"] = T("首次观测到这个值（Unix 秒）", "First observed at (Unix seconds)"),
        ["last_observed_at"] = T("最近观测到这个值（Unix 秒）", "Last observed at (Unix seconds)"),
        ["last_played_at"] = T("Steam 报告的最近游玩时间（Unix 秒）", "Last played time reported by Steam (Unix seconds)"),
        ["internal_name"] = T("成就的内部名称", "Internal achievement name"),
        ["description"] = T("描述", "Description"),
        ["hidden"] = T("是否隐藏成就", "Whether the achievement is hidden"),
        ["global_percent"] = T("全球解锁率（百分比）", "Global unlock rate (percent)"),
        ["unlock_time"] = T("解锁时间（Unix 秒）", "Unlock time (Unix seconds)"),
        ["total"] = T("成就总数", "Total achievements"),
        ["unlocked"] = T("已解锁数", "Unlocked achievements"),
        ["progress_total"] = T("进度接口报告的总数", "Total reported by the progress API"),
        ["progress_unlocked"] = T("进度接口报告的已解锁数", "Unlocked count reported by the progress API"),
        ["synced_at"] = T("同步时间（Unix 秒）", "Sync time (Unix seconds)"),
        ["status"] = T("用户标记的状态", "Status set by the user"),
        ["completed_at"] = T("完成时间（Unix 秒）", "Completion time (Unix seconds)"),
        ["rating"] = T("用户评分", "User rating"),
        ["updated_at"] = T("更新时间（Unix 秒）", "Update time (Unix seconds)"),
        ["package_id"] = T("授权包 ID", "Package ID"),
        ["acquired_at"] = T("入库时间（Unix 秒）", "Acquisition time (Unix seconds)"),
        ["taken_at"] = T("拍摄时间（Unix 秒）", "Time taken (Unix seconds)"),
        ["width"] = T("宽度（像素）", "Width in pixels"),
        ["height"] = T("高度（像素）", "Height in pixels"),
        ["missing_since"] = T("文件消失的时间（Unix 秒），还在时为空", "When the file went missing (Unix seconds), empty if it still exists"),
        ["field"] = T("变化的资料字段", "The profile field that changed"),
        ["value"] = T("新值", "New value"),
        ["previous_value"] = T("旧值", "Previous value"),
        ["observed_at"] = T("观测时间（Unix 秒）", "Observed at (Unix seconds)"),
        ["record_id"] = T("原始记录 ID", "Raw record ID"),
        ["note"] = T("备注", "Note"),
        ["tags"] = T("标签", "Tags"),
        ["friend_steam_id"] = T("好友的 SteamID64", "SteamID64 of the friend"),
        ["friend_persona_name"] = T("当时的好友昵称", "The friend's persona name at the time"),
        ["change_type"] = T("变化类型：state、game、personaName、richPresence、relationship", "Change type: state, game, personaName, richPresence, relationship"),
        ["current_value"] = T("新值（JSON）", "New value (JSON)"),
        ["observer_steam_id"] = T("观察这条状态的本机账号", "The local account that observed this status"),
        ["is_self"] = T("是否账号本人的状态", "Whether this is the account's own status"),
        ["display_text"] = T("解析后的状态文案，解析不了时为空", "Resolved status text, empty when it could not be resolved"),
        ["raw_values"] = T("Steam 给出的原始键值（JSON）", "Raw key-value pairs from Steam (JSON)"),
        ["v_friend_event.previous_value"] = T("旧值（JSON）", "Previous value (JSON)"),
        ["v_rich_presence.steam_id"] = T("状态所属账号的 SteamID64", "SteamID64 of the player the status belongs to"),
        ["v_rich_presence.source"] = T("来源：push 推送，poll 对账轮询", "Source: push, or poll for the reconciliation pass"),
        ["v_session.source"] = T("来源：auto 自动记录，imported 导入，merged 手动合并，mixed 混合", "Source: auto, imported, merged, or mixed")
    };

    internal static BilingualTextDto? DescribeColumn(string view, string column)
        => ColumnDescriptions.GetValueOrDefault($"{view}.{column}") ?? ColumnDescriptions.GetValueOrDefault(column);
}

/// <summary>
/// 指标字典：每个指标的定义、公式、口径和来源，公式都能在语义视图上直接运行，
/// 所以界面上的说明和实际算出来的数字不会各说各话。
/// </summary>
internal static class MetricCatalog
{
    private static BilingualTextDto T(string zh, string en) => new(zh, en);

    private static MetricDefinitionDto M(
        string id, string unit, string evidence, string sql, string[] views,
        BilingualTextDto name, BilingualTextDto definition, BilingualTextDto formula, BilingualTextDto? caveat = null)
        => new()
        {
            Id = id, Unit = unit, Evidence = evidence, Sql = sql, Views = views,
            Name = name, Definition = definition, Formula = formula, Caveat = caveat
        };

    internal static readonly IReadOnlyList<MetricDefinitionDto> Metrics =
    [
        M("total_playtime", "seconds", "recorded",
            "SELECT SUM(duration_seconds) FROM v_session", ["v_session"],
            T("总时长", "Total playtime"),
            T("本应用记录到的全部游玩时长，含挂机。", "All playtime recorded by this app, including idle time."),
            T("Σ 会话时长", "Σ session duration"),
            T("不含安装本应用之前的历史，所以通常小于 Steam 官方时长。", "Excludes history before this app was installed, so it is usually below the official Steam figure.")),
        M("effective_playtime", "seconds", "recorded",
            "SELECT SUM(effective_seconds) FROM v_session", ["v_session"],
            T("有效时长", "Effective playtime"),
            T("扣除挂机之后真正在玩的时长。", "Playtime after removing idle time."),
            T("Σ (会话时长 − 挂机时长)", "Σ (session duration − idle time)"),
            T("挂机判定需要开启挂机检测，且依赖系统的输入空闲时间。", "Idle detection must be on, and relies on the system's input idle time.")),
        M("idle_rate", "percent", "recorded",
            "SELECT 100.0 * SUM(idle_seconds) / NULLIF(SUM(duration_seconds), 0) FROM v_session", ["v_session"],
            T("挂机率", "Idle rate"),
            T("时长中挂机所占的比例。", "Share of playtime spent idle."),
            T("Σ 挂机时长 ÷ Σ 会话时长", "Σ idle time ÷ Σ session duration")),
        M("session_count", "count", "recorded",
            "SELECT COUNT(*) FROM v_session", ["v_session"],
            T("会话数", "Sessions"),
            T("清洗后的游戏局数。", "Number of cleaned sessions."),
            T("COUNT(会话)", "COUNT(sessions)"),
            T("受「最短会话」和「合并间隔」设置影响。", "Affected by the minimum duration and merge gap settings.")),
        M("average_session", "seconds", "recorded",
            "SELECT AVG(duration_seconds) FROM v_session", ["v_session"],
            T("平均每局时长", "Average session"),
            T("每局游戏的平均时长。", "Average length of a session."),
            T("Σ 会话时长 ÷ 会话数", "Σ session duration ÷ sessions")),
        M("longest_session", "seconds", "recorded",
            "SELECT MAX(duration_seconds) FROM v_session", ["v_session"],
            T("最长一局", "Longest session"),
            T("时长最长的一局。", "The longest single session."),
            T("MAX(会话时长)", "MAX(session duration)")),
        M("active_days", "count", "recorded",
            "SELECT COUNT(DISTINCT logical_day) FROM v_daily_playtime", ["v_daily_playtime"],
            T("活跃天数", "Active days"),
            T("有游玩记录的逻辑日数量。", "Logical days with any playtime."),
            T("COUNT(DISTINCT 逻辑日)", "COUNT(DISTINCT logical day)"),
            T("多个账号同一天玩只算一天。", "A day counts once even if several accounts played.")),
        M("daily_average", "seconds", "derived",
            "SELECT SUM(seconds) * 1.0 / NULLIF(COUNT(DISTINCT logical_day), 0) FROM v_daily_playtime", ["v_daily_playtime"],
            T("活跃日均时长", "Average per active day"),
            T("有游玩的日子里平均每天的时长。", "Average playtime on days with any play."),
            T("Σ 时长 ÷ 活跃天数", "Σ playtime ÷ active days")),
        M("games_played", "count", "recorded",
            "SELECT COUNT(DISTINCT app_id) FROM v_session", ["v_session"],
            T("玩过的游戏数", "Games played"),
            T("至少有一局记录的游戏数量。", "Games with at least one recorded session."),
            T("COUNT(DISTINCT 游戏)", "COUNT(DISTINCT game)")),
        M("official_playtime", "seconds", "official",
            "SELECT SUM(minutes) * 60 FROM (SELECT MAX(playtime_minutes) AS minutes FROM v_official_playtime GROUP BY steam_id, app_id)",
            ["v_official_playtime"],
            T("官方总时长", "Official playtime"),
            T("Steam 官方报告的累计时长，取每款游戏观测到的最大值。", "Total playtime reported by Steam, using the highest value observed per game."),
            T("Σ MAX(官方分钟) × 60", "Σ MAX(official minutes) × 60")),
        M("achievements_unlocked", "count", "recorded",
            "SELECT COUNT(*) FROM v_achievement_unlock", ["v_achievement_unlock"],
            T("已解锁成就", "Achievements unlocked"),
            T("档案里记录到的已解锁成就数。", "Unlocked achievements in the archive."),
            T("COUNT(解锁)", "COUNT(unlocks)")),
        M("perfect_games", "count", "derived",
            "SELECT COUNT(*) FROM v_achievement_game WHERE total > 0 AND unlocked >= total", ["v_achievement_game"],
            T("全成就游戏", "Perfect games"),
            T("所有成就都已解锁的游戏数。", "Games with every achievement unlocked."),
            T("COUNT(已解锁 ≥ 总数)", "COUNT(unlocked ≥ total)"),
            T("目前总数含 DLC 和已归档成就，成就集适配后口径会改为与 Steam 一致。", "The total currently includes DLC and archived achievements. It will match Steam once achievement sets are supported.")),
        M("average_completion", "percent", "derived",
            "SELECT 100.0 * AVG(unlocked * 1.0 / total) FROM v_achievement_game WHERE total > 0 AND unlocked > 0", ["v_achievement_game"],
            T("平均完成率", "Average completion"),
            T("至少解锁过一个成就的游戏的平均完成率。", "Average completion across games with at least one unlock."),
            T("AVG(已解锁 ÷ 总数)", "AVG(unlocked ÷ total)"),
            T("与「全成就游戏」相同，成就集适配后口径会变。", "Like perfect games, this changes once achievement sets are supported.")),
        M("screenshots", "count", "recorded",
            "SELECT COUNT(*) FROM v_screenshot", ["v_screenshot"],
            T("截图数", "Screenshots"),
            T("索引到的 Steam 截图数，含文件已删除的。", "Indexed Steam screenshots, including deleted files."),
            T("COUNT(截图)", "COUNT(screenshots)")),
        M("friend_events", "count", "recorded",
            "SELECT COUNT(*) FROM v_friend_event", ["v_friend_event"],
            T("好友动态数", "Friend events"),
            T("手动追踪好友的状态变化记录数。", "Status changes recorded for manually tracked friends."),
            T("COUNT(动态)", "COUNT(events)")),
        M("rich_presence_samples", "count", "recorded",
            "SELECT COUNT(*) FROM v_rich_presence", ["v_rich_presence"],
            T("游戏内状态记录数", "In-game status records"),
            T("落库的游戏内状态变化数。", "In-game status changes stored."),
            T("COUNT(状态记录)", "COUNT(status records)"))
    ];
}
