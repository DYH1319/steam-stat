namespace ElectronNet.Models;

/// <summary>绑定过的任天堂账号。登录凭证不在这里（见 <see cref="NintendoCredential"/>），这张表参与同步。</summary>
public class NintendoAccount
{
    public int Id { get; init; }

    public string NaId { get; init; } = string.Empty;

    public string Nickname { get; set; } = string.Empty;

    public string? IconUrl { get; set; }

    public string? Country { get; set; }

    public bool Mine { get; set; }

    public long LinkedAt { get; set; }

    /// <summary>本机最近一次读取的时间和结果。每台设备自己读，同步过来的值只作参考。</summary>
    public long? LastSyncAt { get; set; }

    public long? LastSuccessAt { get; set; }

    public string? LastError { get; set; }
}

/// <summary>任天堂账号的 session token（经 ISecretStore 加密）。只属于本机：不同步、不进备份、不进导出。</summary>
public class NintendoCredential
{
    public int Id { get; init; }

    public string NaId { get; init; } = string.Empty;

    public string SessionToken { get; set; } = string.Empty;

    public long UpdatedAt { get; set; }
}

/// <summary>游戏名称和封面（按标题 ID + 主机）。语言跟随读取时的界面语言。</summary>
public class NintendoTitle
{
    public int Id { get; init; }

    public string TitleId { get; init; } = string.Empty;

    public string Platform { get; init; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? ImageUrl { get; set; }

    public long UpdatedAt { get; set; }
}

/// <summary>任天堂给的每款游戏的累计值，每个账号 + 游戏 + 主机一条，只保留最新的。</summary>
public class NintendoTitleTotal
{
    public int Id { get; init; }

    public string NaId { get; init; } = string.Empty;

    public string TitleId { get; init; } = string.Empty;

    public string Platform { get; init; } = string.Empty;

    public int TotalMinutes { get; set; }

    public int TotalDays { get; set; }

    public long? FirstPlayedAt { get; set; }

    public long? LastPlayedAt { get; set; }

    public long UpdatedAt { get; set; }
}

/// <summary>
/// 累计分钟数的平台期（按值变化分段，和 playtime_snapshot 一样）：[FirstObservedAt, LastObservedAt] 内读到的都是 TotalMinutes。
/// 相邻两段之间的增量用来推算「大约几点在玩」。
/// </summary>
public class NintendoPlaytimeSnapshot
{
    public int Id { get; init; }

    public string NaId { get; init; } = string.Empty;

    public string TitleId { get; init; } = string.Empty;

    public string Platform { get; init; } = string.Empty;

    public int TotalMinutes { get; init; }

    public long FirstObservedAt { get; set; }

    public long LastObservedAt { get; set; }

    public long? LastPlayedAt { get; set; }
}

/// <summary>任天堂给的日级时长。PlayedDay 是任天堂那边的日期（距 1970-01-01 的天数），最近几天可能还会变，以最新一次读到的为准。</summary>
public class NintendoDailyPlaytime
{
    public int Id { get; init; }

    public string NaId { get; init; } = string.Empty;

    public int PlayedDay { get; init; }

    public string TitleId { get; init; } = string.Empty;

    public string Platform { get; init; } = string.Empty;

    public int Minutes { get; set; }

    public long ObservedAt { get; set; }
}
