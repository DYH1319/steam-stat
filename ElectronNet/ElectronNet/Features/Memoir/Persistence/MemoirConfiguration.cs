using ElectronNet.Enums;
using ElectronNet.Features.Achievements.Persistence;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ElectronNet.Features.Memoir.Persistence;

internal sealed class LifeEventConfiguration : IEntityTypeConfiguration<LifeEvent>
{
    public void Configure(EntityTypeBuilder<LifeEvent> builder)
    {
        builder.ToTable("life_event", t => t.HasComment("用户标注的人生事件"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => e.StartDay, "life_event_start_idx");

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.Title).Column("title", ESqliteTypeName.TEXT, "标题").HasMaxLength(80).IsRequired();
        builder.Property(e => e.Note).Column("note", ESqliteTypeName.TEXT, "备注").HasMaxLength(2000);
        builder.Property(e => e.Kind).Column("kind", ESqliteTypeName.TEXT, "类型").HasMaxLength(16).IsRequired();
        builder.Property(e => e.StartDay).Column("start_day", ESqliteTypeName.INTEGER, "开始的逻辑日（距 1970-01-01 的天数）").IsRequired();
        builder.Property(e => e.EndDay).Column("end_day", ESqliteTypeName.INTEGER, "结束的逻辑日，单日事件为空");
        builder.Property(e => e.Boundary).Column("boundary", ESqliteTypeName.INTEGER, "从这里开始新的一章").IsRequired();
        builder.Property(e => e.CreatedAt).Column("created_at", ESqliteTypeName.INTEGER, "创建时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.UpdatedAt).Column("updated_at", ESqliteTypeName.INTEGER, "修改时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.SyncUid).Column("sync_uid", ESqliteTypeName.TEXT, "多设备同步 ID").HasMaxLength(32);
        builder.HasIndex(e => e.SyncUid, "life_event_sync_uid_uidx").IsUnique();
    }
}

internal sealed class TimeCapsuleConfiguration : IEntityTypeConfiguration<TimeCapsule>
{
    public void Configure(EntityTypeBuilder<TimeCapsule> builder)
    {
        builder.ToTable("time_capsule", t => t.HasComment("时光胶囊：到期前正文不发给渲染层"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => e.OpenAt, "time_capsule_open_idx");

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.Label).Column("label", ESqliteTypeName.TEXT, "封存时可见的标签").HasMaxLength(80);
        builder.Property(e => e.Message).Column("message", ESqliteTypeName.TEXT, "正文").HasMaxLength(4000).IsRequired();
        builder.Property(e => e.CreatedAt).Column("created_at", ESqliteTypeName.INTEGER, "封存时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.OpenAt).Column("open_at", ESqliteTypeName.INTEGER, "最早开启时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.AppId).Column("app_id", ESqliteTypeName.INTEGER, "绑定的游戏：过了日期再启动它才开");
        builder.Property(e => e.ReadyAt).Column("ready_at", ESqliteTypeName.INTEGER, "条件满足的时间");
        builder.Property(e => e.OpenedAt).Column("opened_at", ESqliteTypeName.INTEGER, "开启时间");
        builder.Property(e => e.Context).Column("context", ESqliteTypeName.TEXT, "封存时的档案摘要（JSON）").HasMaxLength(4000);
        builder.Property(e => e.UpdatedAt).Column("updated_at", ESqliteTypeName.INTEGER, "修改时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.SyncUid).Column("sync_uid", ESqliteTypeName.TEXT, "多设备同步 ID").HasMaxLength(32);
        builder.HasIndex(e => e.SyncUid, "time_capsule_sync_uid_uidx").IsUnique();
    }
}

internal sealed class YearInReviewConfiguration : IEntityTypeConfiguration<YearInReviewRecord>
{
    public void Configure(EntityTypeBuilder<YearInReviewRecord> builder)
    {
        builder.ToTable("year_in_review", t => t.HasComment("Steam 年度回顾的原始回复"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.SteamId, e.Year }, "year_in_review_uidx").IsUnique();

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.SteamId).Column("steam_id", ESqliteTypeName.TEXT, "账号 SteamID").HasMaxLength(32).IsRequired();
        builder.Property(e => e.Year).Column("year", ESqliteTypeName.INTEGER, "年份").IsRequired();
        builder.Property(e => e.FetchedAt).Column("fetched_at", ESqliteTypeName.INTEGER, "读取时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.Result).Column("result", ESqliteTypeName.TEXT, "EResult").HasMaxLength(32).IsRequired();
        builder.Property(e => e.Payload).Column("payload", ESqliteTypeName.BLOB, "原始 protobuf");
    }
}

internal sealed class CloudSaveTraceConfiguration : IEntityTypeConfiguration<CloudSaveTrace>
{
    public void Configure(EntityTypeBuilder<CloudSaveTrace> builder)
    {
        builder.ToTable("cloud_save_trace", t => t.HasComment("云存档写入时间，重建史前游玩日历的痕迹"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.SteamId, e.AppId, e.FileKey, e.WrittenAt }, "cloud_save_trace_uidx").IsUnique();
        builder.HasIndex(e => e.WrittenAt, "cloud_save_trace_time_idx");

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.SteamId).Column("steam_id", ESqliteTypeName.TEXT, "账号 SteamID").HasMaxLength(32).IsRequired();
        builder.Property(e => e.AppId).Column("app_id", ESqliteTypeName.INTEGER, "应用 ID").IsRequired();
        builder.Property(e => e.FileKey).Column("file_key", ESqliteTypeName.TEXT, "文件相对路径的哈希").HasMaxLength(16).IsRequired();
        builder.Property(e => e.WrittenAt).Column("written_at", ESqliteTypeName.INTEGER, "写入时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.ObservedAt).Column("observed_at", ESqliteTypeName.INTEGER, "观测时间（Unix 时间戳，秒）").IsRequired();
    }
}
