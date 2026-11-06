using ElectronNet.Enums;
using ElectronNet.Features.Achievements.Persistence;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ElectronNet.Features.Archive.Persistence;

internal sealed class PlatformPlaytimeConfiguration : IEntityTypeConfiguration<PlatformPlaytimeRecord>
{
    public void Configure(EntityTypeBuilder<PlatformPlaytimeRecord> builder)
    {
        builder.ToTable("platform_playtime", t => t.HasComment("Steam 按平台记录的时长，值变化才写"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.SteamId, e.AppId, e.ObservedAt }, "platform_playtime_app_idx");

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.SteamId).Column("steam_id", ESqliteTypeName.TEXT, "账号 SteamID").HasMaxLength(32).IsRequired();
        builder.Property(e => e.AppId).Column("app_id", ESqliteTypeName.INTEGER, "应用 ID").IsRequired();
        builder.Property(e => e.ObservedAt).Column("observed_at", ESqliteTypeName.INTEGER, "观测时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.WindowsMinutes).Column("windows_minutes", ESqliteTypeName.INTEGER, "Windows 时长（分钟）").IsRequired();
        builder.Property(e => e.MacMinutes).Column("mac_minutes", ESqliteTypeName.INTEGER, "macOS 时长（分钟）").IsRequired();
        builder.Property(e => e.LinuxMinutes).Column("linux_minutes", ESqliteTypeName.INTEGER, "Linux 时长（分钟）").IsRequired();
        builder.Property(e => e.DeckMinutes).Column("deck_minutes", ESqliteTypeName.INTEGER, "Steam Deck 时长（分钟）").IsRequired();
        builder.Property(e => e.DisconnectedMinutes).Column("disconnected_minutes", ESqliteTypeName.INTEGER, "离线时长（分钟）").IsRequired();
        builder.Property(e => e.FirstPlayed).Column("first_played", ESqliteTypeName.INTEGER, "官方首玩时间（Unix 时间戳，秒）");
        builder.Property(e => e.LastPlayed).Column("last_played", ESqliteTypeName.INTEGER, "官方最后游玩时间（Unix 时间戳，秒）");
        builder.Property(e => e.FirstWindows).Column("first_windows", ESqliteTypeName.INTEGER, "Windows 首玩时间");
        builder.Property(e => e.FirstMac).Column("first_mac", ESqliteTypeName.INTEGER, "macOS 首玩时间");
        builder.Property(e => e.FirstLinux).Column("first_linux", ESqliteTypeName.INTEGER, "Linux 首玩时间");
        builder.Property(e => e.FirstDeck).Column("first_deck", ESqliteTypeName.INTEGER, "Steam Deck 首玩时间");
        builder.Property(e => e.LastWindows).Column("last_windows", ESqliteTypeName.INTEGER, "Windows 最后游玩时间");
        builder.Property(e => e.LastMac).Column("last_mac", ESqliteTypeName.INTEGER, "macOS 最后游玩时间");
        builder.Property(e => e.LastLinux).Column("last_linux", ESqliteTypeName.INTEGER, "Linux 最后游玩时间");
        builder.Property(e => e.LastDeck).Column("last_deck", ESqliteTypeName.INTEGER, "Steam Deck 最后游玩时间");
    }
}

internal sealed class AppInstallEventConfiguration : IEntityTypeConfiguration<AppInstallEvent>
{
    public void Configure(EntityTypeBuilder<AppInstallEvent> builder)
    {
        builder.ToTable("app_install_event", t => t.HasComment("本机观测到的安装和卸载"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.AppId, e.ObservedAt }, "app_install_event_app_idx");

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.AppId).Column("app_id", ESqliteTypeName.INTEGER, "应用 ID").IsRequired();
        builder.Property(e => e.Kind).Column("kind", ESqliteTypeName.TEXT, "baseline / installed / uninstalled").HasMaxLength(16).IsRequired();
        builder.Property(e => e.ObservedAt).Column("observed_at", ESqliteTypeName.INTEGER, "观测时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.SizeOnDisk).Column("size_on_disk", ESqliteTypeName.INTEGER, "占用空间（字节）");
        builder.Property(e => e.BuildId).Column("build_id", ESqliteTypeName.INTEGER, "版本号");
    }
}

internal sealed class AppPlayerCountConfiguration : IEntityTypeConfiguration<AppPlayerCount>
{
    public void Configure(EntityTypeBuilder<AppPlayerCount> builder)
    {
        builder.ToTable("app_player_count", t => t.HasComment("全球在线人数的每日采样"));
        builder.HasKey(e => new { e.AppId, e.Day });

        builder.Property(e => e.AppId).Column("app_id", ESqliteTypeName.INTEGER, "应用 ID").IsRequired();
        builder.Property(e => e.Day).Column("day", ESqliteTypeName.TEXT, "UTC 日期").HasMaxLength(10).IsRequired();
        builder.Property(e => e.Peak).Column("peak", ESqliteTypeName.INTEGER, "当天采样的最大值").IsRequired();
        builder.Property(e => e.Low).Column("low", ESqliteTypeName.INTEGER, "当天采样的最小值").IsRequired();
        builder.Property(e => e.Samples).Column("samples", ESqliteTypeName.INTEGER, "采样次数，0 表示没有人数数据").IsRequired();
    }
}

internal sealed class StorePageSnapshotConfiguration : IEntityTypeConfiguration<StorePageSnapshot>
{
    public void Configure(EntityTypeBuilder<StorePageSnapshot> builder)
    {
        builder.ToTable("store_page_snapshot", t => t.HasComment("商店页快照，内容变化时写入"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.AppId, e.CapturedAt }, "store_page_snapshot_app_idx");

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.AppId).Column("app_id", ESqliteTypeName.INTEGER, "应用 ID").IsRequired();
        builder.Property(e => e.CapturedAt).Column("captured_at", ESqliteTypeName.INTEGER, "快照时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.Fingerprint).Column("fingerprint", ESqliteTypeName.TEXT, "内容指纹").HasMaxLength(32).IsRequired();
        builder.Property(e => e.Facts).Column("facts", ESqliteTypeName.TEXT, "商店页内容（JSON）").IsRequired();
        builder.Property(e => e.CoverFile).Column("cover_file", ESqliteTypeName.TEXT, "本地封面文件名").HasMaxLength(128);
    }
}

internal sealed class AppArtifactEventConfiguration : IEntityTypeConfiguration<AppArtifactEvent>
{
    public void Configure(EntityTypeBuilder<AppArtifactEvent> builder)
    {
        builder.ToTable("app_artifact_event", t => t.HasComment("数字文物：下架、改名、换封面、疑似关服"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.AppId, e.ObservedAt }, "app_artifact_event_app_idx");

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.AppId).Column("app_id", ESqliteTypeName.INTEGER, "应用 ID").IsRequired();
        builder.Property(e => e.Kind).Column("kind", ESqliteTypeName.TEXT, "事件类型").HasMaxLength(32).IsRequired();
        builder.Property(e => e.ObservedAt).Column("observed_at", ESqliteTypeName.INTEGER, "发现时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.Before).Column("before_value", ESqliteTypeName.TEXT, "变化前");
        builder.Property(e => e.After).Column("after_value", ESqliteTypeName.TEXT, "变化后");
    }
}

internal sealed class WishlistRecordConfiguration : IEntityTypeConfiguration<WishlistRecord>
{
    public void Configure(EntityTypeBuilder<WishlistRecord> builder)
    {
        builder.ToTable("wishlist_record", t => t.HasComment("愿望单历史：加入到移除的每一段"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.SteamId, e.AppId, e.AddedAt }, "wishlist_record_uidx").IsUnique();

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.SteamId).Column("steam_id", ESqliteTypeName.TEXT, "账号 SteamID").HasMaxLength(32).IsRequired();
        builder.Property(e => e.AppId).Column("app_id", ESqliteTypeName.INTEGER, "应用 ID").IsRequired();
        builder.Property(e => e.AddedAt).Column("added_at", ESqliteTypeName.INTEGER, "加入时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.FirstSeenAt).Column("first_seen_at", ESqliteTypeName.INTEGER, "第一次看到的时间").IsRequired();
        builder.Property(e => e.RemovedAt).Column("removed_at", ESqliteTypeName.INTEGER, "移除时间");
        builder.Property(e => e.Priority).Column("priority", ESqliteTypeName.INTEGER, "排序");
    }
}

internal sealed class FriendPlaytimeSnapshotConfiguration : IEntityTypeConfiguration<FriendPlaytimeSnapshot>
{
    public void Configure(EntityTypeBuilder<FriendPlaytimeSnapshot> builder)
    {
        builder.ToTable("friend_playtime_snapshot", t => t.HasComment("手动追踪的好友的公开时长，值变化才写"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.SteamId, e.AppId, e.ObservedAt }, "friend_playtime_snapshot_idx");

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.SteamId).Column("steam_id", ESqliteTypeName.TEXT, "好友 SteamID").HasMaxLength(32).IsRequired();
        builder.Property(e => e.AppId).Column("app_id", ESqliteTypeName.INTEGER, "应用 ID").IsRequired();
        builder.Property(e => e.PlaytimeMinutes).Column("playtime_minutes", ESqliteTypeName.INTEGER, "累计时长（分钟）").IsRequired();
        builder.Property(e => e.ObservedAt).Column("observed_at", ESqliteTypeName.INTEGER, "观测时间（Unix 时间戳，秒）").IsRequired();
    }
}

internal sealed class FamilyMemberConfiguration : IEntityTypeConfiguration<FamilyMemberRecord>
{
    public void Configure(EntityTypeBuilder<FamilyMemberRecord> builder)
    {
        builder.ToTable("family_member", t => t.HasComment("家庭组成员"));
        builder.HasKey(e => new { e.FamilyGroupId, e.SteamId });

        builder.Property(e => e.FamilyGroupId).Column("family_group_id", ESqliteTypeName.TEXT, "家庭组 ID").HasMaxLength(32).IsRequired();
        builder.Property(e => e.SteamId).Column("steam_id", ESqliteTypeName.TEXT, "成员 SteamID").HasMaxLength(32).IsRequired();
        builder.Property(e => e.Name).Column("name", ESqliteTypeName.TEXT, "昵称").HasMaxLength(256);
        builder.Property(e => e.Role).Column("role", ESqliteTypeName.INTEGER, "角色").IsRequired();
        builder.Property(e => e.JoinedAt).Column("joined_at", ESqliteTypeName.INTEGER, "加入时间（Unix 时间戳，秒）");
        builder.Property(e => e.Former).Column("former", ESqliteTypeName.INTEGER, "已退出").IsRequired();
        builder.Property(e => e.UpdatedAt).Column("updated_at", ESqliteTypeName.INTEGER, "更新时间").IsRequired();
    }
}

internal sealed class FamilyPlaytimeConfiguration : IEntityTypeConfiguration<FamilyPlaytimeRecord>
{
    public void Configure(EntityTypeBuilder<FamilyPlaytimeRecord> builder)
    {
        builder.ToTable("family_playtime", t => t.HasComment("家庭共享时长"));
        builder.HasKey(e => new { e.FamilyGroupId, e.SteamId, e.AppId, e.ByOwner });

        builder.Property(e => e.FamilyGroupId).Column("family_group_id", ESqliteTypeName.TEXT, "家庭组 ID").HasMaxLength(32).IsRequired();
        builder.Property(e => e.SteamId).Column("steam_id", ESqliteTypeName.TEXT, "成员 SteamID").HasMaxLength(32).IsRequired();
        builder.Property(e => e.AppId).Column("app_id", ESqliteTypeName.INTEGER, "应用 ID").IsRequired();
        builder.Property(e => e.ByOwner).Column("by_owner", ESqliteTypeName.INTEGER, "来自 entries_by_owner").IsRequired();
        builder.Property(e => e.FirstPlayed).Column("first_played", ESqliteTypeName.INTEGER, "首玩时间（Unix 时间戳，秒）");
        builder.Property(e => e.LatestPlayed).Column("latest_played", ESqliteTypeName.INTEGER, "最后游玩时间（Unix 时间戳，秒）");
        builder.Property(e => e.Seconds).Column("seconds", ESqliteTypeName.INTEGER, "累计秒数").IsRequired();
        builder.Property(e => e.UpdatedAt).Column("updated_at", ESqliteTypeName.INTEGER, "更新时间").IsRequired();
    }
}

internal sealed class WebCaptureConfiguration : IEntityTypeConfiguration<WebCaptureRecord>
{
    public void Configure(EntityTypeBuilder<WebCaptureRecord> builder)
    {
        builder.ToTable("web_capture", t => t.HasComment("保存下来的原始网页"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.SteamId, e.Kind, e.CapturedAt }, "web_capture_kind_idx");

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.SteamId).Column("steam_id", ESqliteTypeName.TEXT, "账号 SteamID").HasMaxLength(32).IsRequired();
        builder.Property(e => e.Kind).Column("kind", ESqliteTypeName.TEXT, "数据种类").HasMaxLength(16).IsRequired();
        builder.Property(e => e.Page).Column("page", ESqliteTypeName.INTEGER, "页码").IsRequired();
        builder.Property(e => e.CapturedAt).Column("captured_at", ESqliteTypeName.INTEGER, "抓取时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.File).Column("file", ESqliteTypeName.TEXT, "文件相对路径").HasMaxLength(256).IsRequired();
        builder.Property(e => e.Sha256).Column("sha256", ESqliteTypeName.TEXT, "内容哈希").HasMaxLength(64).IsRequired();
        builder.Property(e => e.Size).Column("size", ESqliteTypeName.INTEGER, "压缩后字节数").IsRequired();
        builder.Property(e => e.ParserVersion).Column("parser_version", ESqliteTypeName.INTEGER, "解析器版本").IsRequired();
        builder.Property(e => e.State).Column("state", ESqliteTypeName.TEXT, "ok / layout").HasMaxLength(16).IsRequired();
        builder.Property(e => e.Items).Column("items", ESqliteTypeName.INTEGER, "解析出的条目数").IsRequired();
    }
}

internal sealed class PurchaseRecordConfiguration : IEntityTypeConfiguration<PurchaseRecord>
{
    public void Configure(EntityTypeBuilder<PurchaseRecord> builder)
    {
        builder.ToTable("purchase_record", t => t.HasComment("Steam 账单"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.SteamId, e.RowKey }, "purchase_record_uidx").IsUnique();
        builder.HasIndex(e => e.Date, "purchase_record_date_idx");

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.SteamId).Column("steam_id", ESqliteTypeName.TEXT, "账号 SteamID").HasMaxLength(32).IsRequired();
        builder.Property(e => e.RowKey).Column("row_key", ESqliteTypeName.TEXT, "交易号或内容哈希").HasMaxLength(64).IsRequired();
        builder.Property(e => e.Date).Column("date", ESqliteTypeName.INTEGER, "日期（Unix 时间戳，秒，当天 0 点）").IsRequired();
        builder.Property(e => e.Kind).Column("kind", ESqliteTypeName.TEXT, "purchase / gift / ingame / market / wallet / refund / other").HasMaxLength(16).IsRequired();
        builder.Property(e => e.TypeText).Column("type_text", ESqliteTypeName.TEXT, "账单上的类型文字").HasMaxLength(128).IsRequired();
        builder.Property(e => e.TotalCents).Column("total_cents", ESqliteTypeName.INTEGER, "金额 × 100").IsRequired();
        builder.Property(e => e.Currency).Column("currency", ESqliteTypeName.TEXT, "币种").HasMaxLength(8);
        builder.Property(e => e.WalletChangeCents).Column("wallet_change_cents", ESqliteTypeName.INTEGER, "钱包变化 × 100");
        builder.Property(e => e.PaymentMethod).Column("payment_method", ESqliteTypeName.TEXT, "付款方式").HasMaxLength(128);
        builder.Property(e => e.Refunded).Column("refunded", ESqliteTypeName.INTEGER, "已退款").IsRequired();
        builder.Property(e => e.Items).Column("items", ESqliteTypeName.TEXT, "商品和匹配到的应用（JSON）").IsRequired();
        builder.Property(e => e.FirstSeenAt).Column("first_seen_at", ESqliteTypeName.INTEGER, "第一次看到的时间").IsRequired();
    }
}

internal sealed class CommunityReviewConfiguration : IEntityTypeConfiguration<CommunityReviewRecord>
{
    public void Configure(EntityTypeBuilder<CommunityReviewRecord> builder)
    {
        builder.ToTable("community_review", t => t.HasComment("写过的评测"));
        builder.HasKey(e => new { e.SteamId, e.ReviewId });

        builder.Property(e => e.SteamId).Column("steam_id", ESqliteTypeName.TEXT, "账号 SteamID").HasMaxLength(32).IsRequired();
        builder.Property(e => e.ReviewId).Column("review_id", ESqliteTypeName.TEXT, "评测 ID").HasMaxLength(32).IsRequired();
        builder.Property(e => e.AppId).Column("app_id", ESqliteTypeName.INTEGER, "应用 ID").IsRequired();
        builder.Property(e => e.Recommended).Column("recommended", ESqliteTypeName.INTEGER, "推荐").IsRequired();
        builder.Property(e => e.HoursTotal).Column("hours_total", ESqliteTypeName.REAL, "总时长（小时）");
        builder.Property(e => e.HoursAtReview).Column("hours_at_review", ESqliteTypeName.REAL, "写评测时的时长（小时）");
        builder.Property(e => e.PostedAt).Column("posted_at", ESqliteTypeName.INTEGER, "发布时间（Unix 时间戳，秒）");
        builder.Property(e => e.EditedAt).Column("edited_at", ESqliteTypeName.INTEGER, "最后编辑时间");
        builder.Property(e => e.Text).Column("text", ESqliteTypeName.TEXT, "内容").IsRequired();
        builder.Property(e => e.Helpful).Column("helpful", ESqliteTypeName.INTEGER, "觉得有用的人数").IsRequired();
        builder.Property(e => e.Funny).Column("funny", ESqliteTypeName.INTEGER, "觉得欢乐的人数").IsRequired();
        builder.Property(e => e.FirstSeenAt).Column("first_seen_at", ESqliteTypeName.INTEGER, "第一次看到的时间").IsRequired();
        builder.Property(e => e.LastSeenAt).Column("last_seen_at", ESqliteTypeName.INTEGER, "最后看到的时间").IsRequired();
        builder.Property(e => e.MissingSince).Column("missing_since", ESqliteTypeName.INTEGER, "从页面上消失的时间");
    }
}

internal sealed class CommunityFileConfiguration : IEntityTypeConfiguration<CommunityFileRecord>
{
    public void Configure(EntityTypeBuilder<CommunityFileRecord> builder)
    {
        builder.ToTable("community_file", t => t.HasComment("写过的指南和收藏的工坊物品"));
        builder.HasKey(e => new { e.SteamId, e.Kind, e.PublishedFileId });

        builder.Property(e => e.SteamId).Column("steam_id", ESqliteTypeName.TEXT, "账号 SteamID").HasMaxLength(32).IsRequired();
        builder.Property(e => e.Kind).Column("kind", ESqliteTypeName.TEXT, "guides / favorites").HasMaxLength(16).IsRequired();
        builder.Property(e => e.PublishedFileId).Column("published_file_id", ESqliteTypeName.TEXT, "文件 ID").HasMaxLength(32).IsRequired();
        builder.Property(e => e.AppId).Column("app_id", ESqliteTypeName.INTEGER, "应用 ID").IsRequired();
        builder.Property(e => e.Title).Column("title", ESqliteTypeName.TEXT, "标题").IsRequired();
        builder.Property(e => e.Description).Column("description", ESqliteTypeName.TEXT, "简介");
        builder.Property(e => e.Author).Column("author", ESqliteTypeName.TEXT, "作者");
        builder.Property(e => e.PreviewUrl).Column("preview_url", ESqliteTypeName.TEXT, "预览图");
        builder.Property(e => e.FirstSeenAt).Column("first_seen_at", ESqliteTypeName.INTEGER, "第一次看到的时间").IsRequired();
        builder.Property(e => e.LastSeenAt).Column("last_seen_at", ESqliteTypeName.INTEGER, "最后看到的时间").IsRequired();
        builder.Property(e => e.MissingSince).Column("missing_since", ESqliteTypeName.INTEGER, "从页面上消失的时间");
    }
}

internal sealed class CommunityCommentConfiguration : IEntityTypeConfiguration<CommunityCommentRecord>
{
    public void Configure(EntityTypeBuilder<CommunityCommentRecord> builder)
    {
        builder.ToTable("community_comment", t => t.HasComment("资料页收到的留言"));
        builder.HasKey(e => new { e.SteamId, e.CommentId });
        builder.HasIndex(e => new { e.SteamId, e.PostedAt }, "community_comment_time_idx");

        builder.Property(e => e.SteamId).Column("steam_id", ESqliteTypeName.TEXT, "资料页所属账号").HasMaxLength(32).IsRequired();
        builder.Property(e => e.CommentId).Column("comment_id", ESqliteTypeName.TEXT, "留言 ID").HasMaxLength(32).IsRequired();
        builder.Property(e => e.AuthorSteamId).Column("author_steam_id", ESqliteTypeName.TEXT, "留言人 SteamID").HasMaxLength(32);
        builder.Property(e => e.AuthorName).Column("author_name", ESqliteTypeName.TEXT, "留言人昵称").IsRequired();
        builder.Property(e => e.PostedAt).Column("posted_at", ESqliteTypeName.INTEGER, "留言时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.Text).Column("text", ESqliteTypeName.TEXT, "内容").IsRequired();
        builder.Property(e => e.FirstSeenAt).Column("first_seen_at", ESqliteTypeName.INTEGER, "第一次看到的时间").IsRequired();
        builder.Property(e => e.LastSeenAt).Column("last_seen_at", ESqliteTypeName.INTEGER, "最后看到的时间").IsRequired();
        builder.Property(e => e.MissingSince).Column("missing_since", ESqliteTypeName.INTEGER, "从页面上消失的时间");
    }
}

internal sealed class RecordingTimelineConfiguration : IEntityTypeConfiguration<RecordingTimelineRecord>
{
    public void Configure(EntityTypeBuilder<RecordingTimelineRecord> builder)
    {
        builder.ToTable("recording_timeline", t => t.HasComment("Steam 录像时间线"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.AccountId, e.TimelineId }, "recording_timeline_uidx").IsUnique();
        builder.HasIndex(e => new { e.AppId, e.StartedAt }, "recording_timeline_app_idx");

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.AccountId).Column("account_id", ESqliteTypeName.INTEGER, "32 位账号 ID").IsRequired();
        builder.Property(e => e.SteamId).Column("steam_id", ESqliteTypeName.TEXT, "账号 SteamID").HasMaxLength(32).IsRequired();
        builder.Property(e => e.TimelineId).Column("timeline_id", ESqliteTypeName.TEXT, "时间线 ID").HasMaxLength(128).IsRequired();
        builder.Property(e => e.AppId).Column("app_id", ESqliteTypeName.INTEGER, "应用 ID").IsRequired();
        builder.Property(e => e.StartedAt).Column("started_at", ESqliteTypeName.INTEGER, "开始时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.DurationMs).Column("duration_ms", ESqliteTypeName.INTEGER, "时长（毫秒）").IsRequired();
        builder.Property(e => e.Markers).Column("markers", ESqliteTypeName.TEXT, "时间线标记（JSON）").IsRequired();
        builder.Property(e => e.IndexedAt).Column("indexed_at", ESqliteTypeName.INTEGER, "索引时间").IsRequired();
    }
}

internal sealed class RecordingMediaConfiguration : IEntityTypeConfiguration<RecordingMediaRecord>
{
    public void Configure(EntityTypeBuilder<RecordingMediaRecord> builder)
    {
        builder.ToTable("recording_media", t => t.HasComment("磁盘上的录像和片段"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.AccountId, e.MediaId }, "recording_media_uidx").IsUnique();
        builder.HasIndex(e => new { e.AppId, e.StartedAt }, "recording_media_app_idx");

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.AccountId).Column("account_id", ESqliteTypeName.INTEGER, "32 位账号 ID").IsRequired();
        builder.Property(e => e.SteamId).Column("steam_id", ESqliteTypeName.TEXT, "账号 SteamID").HasMaxLength(32).IsRequired();
        builder.Property(e => e.MediaId).Column("media_id", ESqliteTypeName.TEXT, "文件夹名").HasMaxLength(128).IsRequired();
        builder.Property(e => e.Kind).Column("kind", ESqliteTypeName.TEXT, "background / clip").HasMaxLength(16).IsRequired();
        builder.Property(e => e.AppId).Column("app_id", ESqliteTypeName.INTEGER, "应用 ID").IsRequired();
        builder.Property(e => e.StartedAt).Column("started_at", ESqliteTypeName.INTEGER, "开始时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.DurationMs).Column("duration_ms", ESqliteTypeName.INTEGER, "时长（毫秒）").IsRequired();
        builder.Property(e => e.SizeBytes).Column("size_bytes", ESqliteTypeName.INTEGER, "大小（字节）").IsRequired();
        builder.Property(e => e.Name).Column("name", ESqliteTypeName.TEXT, "片段名称").HasMaxLength(256);
        builder.Property(e => e.TimelineIds).Column("timeline_ids", ESqliteTypeName.TEXT, "关联的时间线（JSON）");
        builder.Property(e => e.FolderPath).Column("folder_path", ESqliteTypeName.TEXT, "文件夹路径").IsRequired();
        builder.Property(e => e.ThumbnailPath).Column("thumbnail_path", ESqliteTypeName.TEXT, "缩略图路径");
        builder.Property(e => e.Markers).Column("markers", ESqliteTypeName.TEXT, "片段标记（JSON）");
        builder.Property(e => e.IndexedAt).Column("indexed_at", ESqliteTypeName.INTEGER, "索引时间").IsRequired();
        builder.Property(e => e.MissingSince).Column("missing_since", ESqliteTypeName.INTEGER, "文件消失的时间");
    }
}

internal sealed class CollectorStateConfiguration : IEntityTypeConfiguration<CollectorState>
{
    public void Configure(EntityTypeBuilder<CollectorState> builder)
    {
        builder.ToTable("collector_state", t => t.HasComment("档案采集器的运行状态"));
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.TEXT, "采集器 ID").HasMaxLength(32).IsRequired().ValueGeneratedNever();
        builder.Property(e => e.LastRunAt).Column("last_run_at", ESqliteTypeName.INTEGER, "上次运行时间（Unix 时间戳，秒）");
        builder.Property(e => e.LastSuccessAt).Column("last_success_at", ESqliteTypeName.INTEGER, "上次成功时间（Unix 时间戳，秒）");
        builder.Property(e => e.LastError).Column("last_error", ESqliteTypeName.TEXT, "上次失败的原因").HasMaxLength(32);
        builder.Property(e => e.LastItems).Column("last_items", ESqliteTypeName.INTEGER, "上次采集到的条目数");
        builder.Property(e => e.Data).Column("data", ESqliteTypeName.TEXT, "采集器自己的状态（JSON）");
    }
}
