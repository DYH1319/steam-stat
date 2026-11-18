using ElectronNet.Enums;
using ElectronNet.Features.Achievements.Persistence;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ElectronNet.Features.Friends.Persistence;

internal sealed class PresenceGroupRecordConfiguration : IEntityTypeConfiguration<PresenceGroupRecord>
{
    public void Configure(EntityTypeBuilder<PresenceGroupRecord> builder)
    {
        builder.ToTable("presence_group_record", t => t.HasComment("大厅和服务器的变化，用于判定是否在同一局"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.SteamId, e.ObservedAt }, "presence_group_record_steam_id_observed_at_idx");
        builder.HasIndex(e => e.ObservedAt, "presence_group_record_observed_at_idx");

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.AccountName).Column("account_name", ESqliteTypeName.TEXT, "观察到的本机账号").HasMaxLength(128).IsRequired();
        builder.Property(e => e.SteamId).Column("steam_id", ESqliteTypeName.TEXT, "玩家 SteamID，可以是账号本人").HasMaxLength(32).IsRequired();
        builder.Property(e => e.IsSelf).Column("is_self", ESqliteTypeName.INTEGER, "是否账号本人").IsRequired();
        builder.Property(e => e.AppId).Column("app_id", ESqliteTypeName.INTEGER, "应用 ID").IsRequired();
        builder.Property(e => e.Kind).Column("kind", ESqliteTypeName.TEXT, "分组类型：lobby / server").HasMaxLength(16).IsRequired();
        builder.Property(e => e.GroupKey).Column("group_key", ESqliteTypeName.TEXT, "分组的哈希，空表示离开").HasMaxLength(32).IsRequired();
        builder.Property(e => e.ObservedAt).Column("observed_at", ESqliteTypeName.INTEGER, "观测时间（Unix 时间戳，秒）").IsRequired();
    }
}

internal sealed class FriendObservationWindowConfiguration : IEntityTypeConfiguration<FriendObservationWindow>
{
    public void Configure(EntityTypeBuilder<FriendObservationWindow> builder)
    {
        builder.ToTable("friend_observation_window", t => t.HasComment("应用能看到好友状态的时间段"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.AccountName, e.StartedAt }, "friend_observation_window_account_start_idx");

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.AccountName).Column("account_name", ESqliteTypeName.TEXT, "本机账号").HasMaxLength(128).IsRequired();
        builder.Property(e => e.StartedAt).Column("started_at", ESqliteTypeName.INTEGER, "开始（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.EndedAt).Column("ended_at", ESqliteTypeName.INTEGER, "结束或最后一次心跳（Unix 时间戳，秒）").IsRequired();
    }
}

internal sealed class FriendSinceRecordConfiguration : IEntityTypeConfiguration<FriendSinceRecord>
{
    public void Configure(EntityTypeBuilder<FriendSinceRecord> builder)
    {
        builder.ToTable("friend_since", t => t.HasComment("Steam 给的成为好友的时间"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.AccountName, e.FriendSteamId }, "friend_since_uidx").IsUnique();

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.AccountName).Column("account_name", ESqliteTypeName.TEXT, "本机账号").HasMaxLength(128).IsRequired();
        builder.Property(e => e.FriendSteamId).Column("friend_steam_id", ESqliteTypeName.TEXT, "好友 SteamID").HasMaxLength(32).IsRequired();
        builder.Property(e => e.FriendSince).Column("friend_since", ESqliteTypeName.INTEGER, "成为好友的时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.ObservedAt).Column("observed_at", ESqliteTypeName.INTEGER, "读取时间（Unix 时间戳，秒）").IsRequired();
    }
}

internal sealed class FriendLibraryItemConfiguration : IEntityTypeConfiguration<FriendLibraryItem>
{
    public void Configure(EntityTypeBuilder<FriendLibraryItem> builder)
    {
        builder.ToTable("friend_library_item", t => t.HasComment("手动追踪的好友的游戏库，记录入库时间"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.SteamId, e.AppId }, "friend_library_item_uidx").IsUnique();
        builder.HasIndex(e => e.FirstSeenAt, "friend_library_item_first_seen_idx");

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.SteamId).Column("steam_id", ESqliteTypeName.TEXT, "好友 SteamID").HasMaxLength(32).IsRequired();
        builder.Property(e => e.AppId).Column("app_id", ESqliteTypeName.INTEGER, "应用 ID").IsRequired();
        builder.Property(e => e.FirstSeenAt).Column("first_seen_at", ESqliteTypeName.INTEGER, "第一次看到（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.IsBaseline).Column("is_baseline", ESqliteTypeName.INTEGER, "第一次采集就有，入库时间不知道").IsRequired();
        builder.Property(e => e.RemovedAt).Column("removed_at", ESqliteTypeName.INTEGER, "从游戏库消失的时间");
    }
}

internal sealed class FriendAchievementProgressConfiguration : IEntityTypeConfiguration<FriendAchievementProgress>
{
    public void Configure(EntityTypeBuilder<FriendAchievementProgress> builder)
    {
        builder.ToTable("friend_achievement_progress", t => t.HasComment("手动追踪的好友的成就进度，值变化才写"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.SteamId, e.AppId, e.ObservedAt }, "friend_achievement_progress_idx");

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.SteamId).Column("steam_id", ESqliteTypeName.TEXT, "好友 SteamID").HasMaxLength(32).IsRequired();
        builder.Property(e => e.AppId).Column("app_id", ESqliteTypeName.INTEGER, "应用 ID").IsRequired();
        builder.Property(e => e.Unlocked).Column("unlocked", ESqliteTypeName.INTEGER, "已解锁").IsRequired();
        builder.Property(e => e.Total).Column("total", ESqliteTypeName.INTEGER, "总数").IsRequired();
        builder.Property(e => e.ObservedAt).Column("observed_at", ESqliteTypeName.INTEGER, "观测时间（Unix 时间戳，秒）").IsRequired();
    }
}

internal sealed class TogetherRecordConfiguration : IEntityTypeConfiguration<TogetherRecord>
{
    public void Configure(EntityTypeBuilder<TogetherRecord> builder)
    {
        builder.ToTable("together_record", t => t.HasComment("一局游戏里和一位好友在一起的时间，由会话标签服务算出"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => e.SessionId, "together_record_session_idx");
        builder.HasIndex(e => new { e.FriendSteamId, e.StartTime }, "together_record_friend_start_idx");

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.SessionId).Column("session_id", ESqliteTypeName.INTEGER, "use_app_record.id").IsRequired();
        builder.Property(e => e.SteamId).Column("steam_id", ESqliteTypeName.TEXT, "账号 SteamID").HasMaxLength(32).IsRequired();
        builder.Property(e => e.FriendSteamId).Column("friend_steam_id", ESqliteTypeName.TEXT, "好友 SteamID").HasMaxLength(32).IsRequired();
        builder.Property(e => e.AppId).Column("app_id", ESqliteTypeName.INTEGER, "应用 ID").IsRequired();
        builder.Property(e => e.StartTime).Column("start_time", ESqliteTypeName.INTEGER, "第一段重叠的开始（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.EndTime).Column("end_time", ESqliteTypeName.INTEGER, "最后一段重叠的结束（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.Seconds).Column("seconds", ESqliteTypeName.INTEGER, "在一起的秒数").IsRequired();
        builder.Property(e => e.ConfirmedSeconds).Column("confirmed_seconds", ESqliteTypeName.INTEGER, "其中确认同组的秒数").IsRequired();
    }
}

internal sealed class ExchangeCardRecordConfiguration : IEntityTypeConfiguration<ExchangeCardRecord>
{
    public void Configure(EntityTypeBuilder<ExchangeCardRecord> builder)
    {
        builder.ToTable("exchange_card", t => t.HasComment("导入的交换卡"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.SenderSteamId, e.GeneratedAt }, "exchange_card_uidx").IsUnique();

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.SenderSteamId).Column("sender_steam_id", ESqliteTypeName.TEXT, "发卡人 SteamID").HasMaxLength(32).IsRequired();
        builder.Property(e => e.SenderName).Column("sender_name", ESqliteTypeName.TEXT, "发卡人昵称").HasMaxLength(64).IsRequired();
        builder.Property(e => e.Year).Column("year", ESqliteTypeName.INTEGER, "卡上的年份").IsRequired();
        builder.Property(e => e.GeneratedAt).Column("generated_at", ESqliteTypeName.INTEGER, "生成时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.ImportedAt).Column("imported_at", ESqliteTypeName.INTEGER, "导入时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.Payload).Column("payload", ESqliteTypeName.TEXT, "卡的内容（JSON）").IsRequired();
    }
}
