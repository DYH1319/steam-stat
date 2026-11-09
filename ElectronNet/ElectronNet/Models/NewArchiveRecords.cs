namespace ElectronNet.Models;

/// <summary>
/// Steam 按平台记录的累计时长。值变化才写一条，所以这张表同时是平台时长的历史。
/// </summary>
public class PlatformPlaytimeRecord
{
    public int Id { get; init; }

    public string SteamId { get; init; } = string.Empty;

    public int AppId { get; init; }

    public long ObservedAt { get; init; }

    public int WindowsMinutes { get; init; }

    public int MacMinutes { get; init; }

    public int LinuxMinutes { get; init; }

    public int DeckMinutes { get; init; }

    public int DisconnectedMinutes { get; init; }

    public long? FirstPlayed { get; init; }

    public long? LastPlayed { get; init; }

    public long? FirstWindows { get; init; }

    public long? FirstMac { get; init; }

    public long? FirstLinux { get; init; }

    public long? FirstDeck { get; init; }

    public long? LastWindows { get; init; }

    public long? LastMac { get; init; }

    public long? LastLinux { get; init; }

    public long? LastDeck { get; init; }
}

/// <summary>本机观测到的安装和卸载。第一次看到已安装的游戏只是基线。</summary>
public class AppInstallEvent
{
    public int Id { get; init; }

    public int AppId { get; init; }

    /// <summary>baseline / installed / uninstalled</summary>
    public string Kind { get; init; } = string.Empty;

    public long ObservedAt { get; init; }

    public long? SizeOnDisk { get; init; }

    public int? BuildId { get; init; }
}

/// <summary>一款游戏一天的在线人数采样。Samples 为 0 表示这款游戏没有人数数据。</summary>
public class AppPlayerCount
{
    public int AppId { get; init; }

    /// <summary>UTC 日期 yyyy-MM-dd。</summary>
    public string Day { get; init; } = string.Empty;

    public int Peak { get; set; }

    public int Low { get; set; }

    public int Samples { get; set; }
}

/// <summary>商店页快照，内容变化时才写一条。价格和评测在 app_store_status。</summary>
public class StorePageSnapshot
{
    public int Id { get; init; }

    public int AppId { get; init; }

    public long CapturedAt { get; init; }

    public string Fingerprint { get; init; } = string.Empty;

    /// <summary>StorePageFacts 的 JSON。</summary>
    public string Facts { get; init; } = "{}";

    /// <summary>本地保存的封面文件名，在 {UserData}/StoreArchive/covers 下。</summary>
    public string? CoverFile { get; set; }
}

/// <summary>数字文物的变化：下架、重新上架、改名、换封面、换发行商、疑似关服。</summary>
public class AppArtifactEvent
{
    public int Id { get; init; }

    public int AppId { get; init; }

    public string Kind { get; init; } = string.Empty;

    public long ObservedAt { get; init; }

    public string? Before { get; init; }

    public string? After { get; init; }
}

/// <summary>愿望单上的一段：加入到移除。重新加入是新的一段。结果（买了还是放弃）在读取时看入库记录。</summary>
public class WishlistRecord
{
    public int Id { get; init; }

    public string SteamId { get; init; } = string.Empty;

    public int AppId { get; init; }

    /// <summary>Steam 给的加入时间，没有时是第一次看到的时间。</summary>
    public long AddedAt { get; init; }

    public long FirstSeenAt { get; init; }

    public long? RemovedAt { get; set; }

    public int? Priority { get; set; }
}

/// <summary>手动追踪的好友的公开累计时长，值变化才写。第一条是基线。</summary>
public class FriendPlaytimeSnapshot
{
    public long Id { get; init; }

    public string SteamId { get; init; } = string.Empty;

    public int AppId { get; init; }

    public int PlaytimeMinutes { get; init; }

    public long ObservedAt { get; init; }
}

/// <summary>家庭组成员。</summary>
public class FamilyMemberRecord
{
    public string FamilyGroupId { get; init; } = string.Empty;

    public string SteamId { get; init; } = string.Empty;

    public string? Name { get; set; }

    public int Role { get; set; }

    public long? JoinedAt { get; set; }

    public bool Former { get; set; }

    public long UpdatedAt { get; set; }
}

/// <summary>家庭共享时长：谁玩了什么多久。ByOwner 是接口的第二份列表，含义待实机确认。</summary>
public class FamilyPlaytimeRecord
{
    public string FamilyGroupId { get; init; } = string.Empty;

    public string SteamId { get; init; } = string.Empty;

    public int AppId { get; init; }

    public bool ByOwner { get; init; }

    public long? FirstPlayed { get; set; }

    public long? LatestPlayed { get; set; }

    public long Seconds { get; set; }

    public long UpdatedAt { get; set; }
}

/// <summary>保存下来的一个原始网页。文件在 {UserData}/WebArchive 下。</summary>
public class WebCaptureRecord
{
    public int Id { get; init; }

    public string SteamId { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    /// <summary>第几页，账单从 0 开始。</summary>
    public int Page { get; init; }

    public long CapturedAt { get; init; }

    /// <summary>相对 {UserData}/WebArchive 的路径。</summary>
    public string File { get; init; } = string.Empty;

    public string Sha256 { get; init; } = string.Empty;

    public long Size { get; init; }

    /// <summary>解析这份页面的解析器版本。低于当前版本时启动后重新解析。</summary>
    public int ParserVersion { get; set; }

    /// <summary>ok / layout</summary>
    public string State { get; set; } = "ok";

    public int Items { get; set; }
}

/// <summary>账单上的一行。Items 是 PurchaseItemMatch 的 JSON，重新匹配时整行更新。</summary>
public class PurchaseRecord
{
    public int Id { get; init; }

    public string SteamId { get; init; } = string.Empty;

    /// <summary>有交易号时是 txn:号，否则是内容哈希。</summary>
    public string RowKey { get; init; } = string.Empty;

    public long Date { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string TypeText { get; set; } = string.Empty;

    public long TotalCents { get; set; }

    public string? Currency { get; set; }

    public long? WalletChangeCents { get; set; }

    public string? PaymentMethod { get; set; }

    public bool Refunded { get; set; }

    public string Items { get; set; } = "[]";

    public long FirstSeenAt { get; init; }
}

/// <summary>写过的评测。从页面上消失后保留，标记 MissingSince。</summary>
public class CommunityReviewRecord
{
    public string SteamId { get; init; } = string.Empty;

    public string ReviewId { get; init; } = string.Empty;

    public int AppId { get; set; }

    public bool Recommended { get; set; }

    public double? HoursTotal { get; set; }

    public double? HoursAtReview { get; set; }

    public long? PostedAt { get; set; }

    public long? EditedAt { get; set; }

    public string Text { get; set; } = string.Empty;

    public int Helpful { get; set; }

    public int Funny { get; set; }

    public long FirstSeenAt { get; init; }

    public long LastSeenAt { get; set; }

    public long? MissingSince { get; set; }
}

/// <summary>写过的指南或收藏的工坊物品。</summary>
public class CommunityFileRecord
{
    public string SteamId { get; init; } = string.Empty;

    /// <summary>guides / favorites</summary>
    public string Kind { get; init; } = string.Empty;

    public string PublishedFileId { get; init; } = string.Empty;

    public int AppId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? Author { get; set; }

    public string? PreviewUrl { get; set; }

    public long FirstSeenAt { get; init; }

    public long LastSeenAt { get; set; }

    public long? MissingSince { get; set; }
}

/// <summary>资料页收到的留言。别人写的内容，只存在本机，不进同步和视图。</summary>
public class CommunityCommentRecord
{
    public string SteamId { get; init; } = string.Empty;

    public string CommentId { get; init; } = string.Empty;

    public string? AuthorSteamId { get; set; }

    public string AuthorName { get; set; } = string.Empty;

    public long PostedAt { get; set; }

    public string Text { get; set; } = string.Empty;

    public long FirstSeenAt { get; init; }

    public long LastSeenAt { get; set; }

    public long? MissingSince { get; set; }
}

/// <summary>一条录像时间线：一次游戏过程。视频被 Steam 清理之后，这些标记还在。</summary>
public class RecordingTimelineRecord
{
    public int Id { get; init; }

    public long AccountId { get; init; }

    public string SteamId { get; set; } = string.Empty;

    public string TimelineId { get; init; } = string.Empty;

    public int AppId { get; set; }

    public long StartedAt { get; set; }

    public long DurationMs { get; set; }

    /// <summary>RecordingMarker 的 JSON 数组。只增不减：Steam 清理索引后标记不会跟着消失。</summary>
    public string Markers { get; set; } = "[]";

    public long IndexedAt { get; set; }
}

/// <summary>磁盘上的一段录像或片段。文件被删除后保留记录，标记 MissingSince。</summary>
public class RecordingMediaRecord
{
    public int Id { get; init; }

    public long AccountId { get; init; }

    public string SteamId { get; set; } = string.Empty;

    /// <summary>文件夹名：bg_… 或 clip_…。</summary>
    public string MediaId { get; init; } = string.Empty;

    /// <summary>background / clip</summary>
    public string Kind { get; init; } = string.Empty;

    public int AppId { get; set; }

    public long StartedAt { get; set; }

    public long DurationMs { get; set; }

    public long SizeBytes { get; set; }

    public string? Name { get; set; }

    public string? TimelineIds { get; set; }

    public string FolderPath { get; set; } = string.Empty;

    public string? ThumbnailPath { get; set; }

    public string? Markers { get; set; }

    public long IndexedAt { get; set; }

    public long? MissingSince { get; set; }
}

/// <summary>每个采集器的运行状态。</summary>
public class CollectorState
{
    public string Id { get; init; } = string.Empty;

    public long? LastRunAt { get; set; }

    public long? LastSuccessAt { get; set; }

    /// <summary>上次失败的原因：signedOut / layout / network / private。成功时清空。</summary>
    public string? LastError { get; set; }

    public int? LastItems { get; set; }

    /// <summary>采集器自己的小状态（比如愿望单连续空结果的次数），JSON。</summary>
    public string? Data { get; set; }
}
