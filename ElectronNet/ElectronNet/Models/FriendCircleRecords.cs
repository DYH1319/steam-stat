namespace ElectronNet.Models;

/// <summary>大厅或服务器的变化：账号本人和手动追踪的好友，值变化才写。分组键是哈希，空表示离开。</summary>
public class PresenceGroupRecord
{
    public long Id { get; init; }

    public string AccountName { get; init; } = string.Empty;

    public string SteamId { get; init; } = string.Empty;

    public bool IsSelf { get; init; }

    public int AppId { get; init; }

    /// <summary>lobby | server</summary>
    public string Kind { get; init; } = string.Empty;

    public string GroupKey { get; init; } = string.Empty;

    public long ObservedAt { get; init; }
}

/// <summary>应用登录着 Steam、能看到好友状态的时间段。算在线概率时用来当分母。</summary>
public class FriendObservationWindow
{
    public long Id { get; init; }

    public string AccountName { get; init; } = string.Empty;

    public long StartedAt { get; init; }

    public long EndedAt { get; set; }
}

/// <summary>Steam Web API 给的成为好友的时间。</summary>
public class FriendSinceRecord
{
    public int Id { get; init; }

    public string AccountName { get; init; } = string.Empty;

    public string FriendSteamId { get; init; } = string.Empty;

    /// <summary>Unix 秒，Steam 给 0 时不写这一行。</summary>
    public long FriendSince { get; set; }

    public long ObservedAt { get; set; }
}

/// <summary>手动追踪的好友的游戏库，用来知道对方什么时候入了哪款游戏。第一次采集的全是基线。</summary>
public class FriendLibraryItem
{
    public long Id { get; init; }

    public string SteamId { get; init; } = string.Empty;

    public int AppId { get; init; }

    public long FirstSeenAt { get; init; }

    /// <summary>第一次采集时就有：入库时间不知道，只知道不晚于 FirstSeenAt。</summary>
    public bool IsBaseline { get; init; }

    public long? RemovedAt { get; set; }
}

/// <summary>手动追踪的好友的成就进度，值变化才写。</summary>
public class FriendAchievementProgress
{
    public long Id { get; init; }

    public string SteamId { get; init; } = string.Empty;

    public int AppId { get; init; }

    public int Unlocked { get; init; }

    public int Total { get; init; }

    public long ObservedAt { get; init; }
}

/// <summary>一局游戏里和一位好友在一起的时间，由会话标签服务算出，不参与同步。</summary>
public class TogetherRecord
{
    public long Id { get; init; }

    public long SessionId { get; init; }

    public string SteamId { get; init; } = string.Empty;

    public string FriendSteamId { get; init; } = string.Empty;

    public int AppId { get; init; }

    public long StartTime { get; init; }

    public long EndTime { get; init; }

    public long Seconds { get; init; }

    /// <summary>其中确认在同一组的秒数。</summary>
    public long ConfirmedSeconds { get; init; }
}

/// <summary>导入的交换卡。Payload 是解开后的 JSON。</summary>
public class ExchangeCardRecord
{
    public int Id { get; init; }

    public string SenderSteamId { get; init; } = string.Empty;

    public string SenderName { get; init; } = string.Empty;

    public int Year { get; init; }

    public long GeneratedAt { get; init; }

    public long ImportedAt { get; init; }

    public string Payload { get; init; } = "{}";
}
