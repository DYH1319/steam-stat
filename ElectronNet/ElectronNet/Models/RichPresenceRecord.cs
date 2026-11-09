namespace ElectronNet.Models;

/// <summary>
/// 一次游戏内状态变化。原始键值是事实，解析后的文案只是当时能解析出来的结果：
/// 本地化暂时拿不到时文案为空，以后可以用原始键值重新解析。
/// </summary>
public class RichPresenceRecord
{
    public long Id { get; init; }

    /// <summary>观察到这条状态的本机账号。</summary>
    public string AccountName { get; init; } = string.Empty;

    /// <summary>状态所属的玩家，可以是账号本人。</summary>
    public string SteamId { get; init; } = string.Empty;

    public bool IsSelf { get; init; }

    public int AppId { get; init; }

    /// <summary>按键名排序的 JSON 对象。空对象表示状态被清空。</summary>
    public string RawValues { get; init; } = "{}";

    public string? DisplayText { get; set; }

    /// <summary>push | poll</summary>
    public string Source { get; init; } = "push";

    public long ObservedAt { get; init; }
}
