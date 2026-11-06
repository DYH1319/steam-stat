using ElectronNet.Enums;
using ElectronNet.Features.Achievements.Persistence;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ElectronNet.Features.Insights.Persistence;

internal sealed class AppBuildRecordConfiguration : IEntityTypeConfiguration<AppBuildRecord>
{
    public void Configure(EntityTypeBuilder<AppBuildRecord> builder)
    {
        builder.ToTable("app_build_history", t => t.HasComment("游戏版本史（本机 ACF 观测）"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.AppId, e.ObservedAt }, "app_build_history_app_idx");

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.AppId).Column("app_id", ESqliteTypeName.INTEGER, "应用 ID").IsRequired();
        builder.Property(e => e.BuildId).Column("build_id", ESqliteTypeName.INTEGER, "版本号").IsRequired();
        builder.Property(e => e.ObservedAt).Column("observed_at", ESqliteTypeName.INTEGER, "版本出现时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.IsBaseline).Column("is_baseline", ESqliteTypeName.INTEGER, "第一次看到这款游戏，不算更新").IsRequired();
    }
}

internal sealed class AppStoreStatusConfiguration : IEntityTypeConfiguration<AppStoreStatus>
{
    public void Configure(EntityTypeBuilder<AppStoreStatus> builder)
    {
        builder.ToTable("app_store_status", t => t.HasComment("游戏的商店公开状态（美区）"));
        builder.HasKey(e => e.AppId);

        builder.Property(e => e.AppId).Column("app_id", ESqliteTypeName.INTEGER, "应用 ID").IsRequired().ValueGeneratedNever();
        builder.Property(e => e.Visible).Column("visible", ESqliteTypeName.INTEGER, "商店页是否可见").IsRequired();
        builder.Property(e => e.Purchasable).Column("purchasable", ESqliteTypeName.INTEGER, "是否有购买选项").IsRequired();
        builder.Property(e => e.IsFree).Column("is_free", ESqliteTypeName.INTEGER, "是否免费").IsRequired();
        builder.Property(e => e.PriceCents).Column("price_cents", ESqliteTypeName.INTEGER, "美区原价（美分）");
        builder.Property(e => e.ReviewCount).Column("review_count", ESqliteTypeName.INTEGER, "评测数");
        builder.Property(e => e.ReviewPercent).Column("review_percent", ESqliteTypeName.INTEGER, "好评率（0~100）");
        builder.Property(e => e.Franchise).Column("franchise", ESqliteTypeName.TEXT, "系列名").HasMaxLength(256);
        builder.Property(e => e.ReleaseDate).Column("release_date", ESqliteTypeName.INTEGER, "发行日期（Unix 时间戳，秒）");
        builder.Property(e => e.DelistedAt).Column("delisted_at", ESqliteTypeName.INTEGER, "第一次发现下架的时间（Unix 时间戳，秒）");
        builder.Property(e => e.CheckedAt).Column("checked_at", ESqliteTypeName.INTEGER, "上次检查时间（Unix 时间戳，秒）").IsRequired();
    }
}

internal sealed class WeeklyIssueConfiguration : IEntityTypeConfiguration<WeeklyIssue>
{
    public void Configure(EntityTypeBuilder<WeeklyIssue> builder)
    {
        builder.ToTable("weekly_issue", t => t.HasComment("档案周刊，生成后冻结"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.Kind, e.IssueKey }, "weekly_issue_key_uidx").IsUnique();
        builder.HasIndex(e => e.GeneratedAt, "weekly_issue_generated_idx");

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.Kind).Column("kind", ESqliteTypeName.TEXT, "weekly / extra").HasMaxLength(16).IsRequired();
        builder.Property(e => e.IssueKey).Column("issue_key", ESqliteTypeName.TEXT, "去重键").HasMaxLength(128).IsRequired();
        builder.Property(e => e.Number).Column("number", ESqliteTypeName.INTEGER, "期号").IsRequired();
        builder.Property(e => e.FromDay).Column("from_day", ESqliteTypeName.TEXT, "起始逻辑日").HasMaxLength(10).IsRequired();
        builder.Property(e => e.ToDay).Column("to_day", ESqliteTypeName.TEXT, "结束逻辑日").HasMaxLength(10).IsRequired();
        builder.Property(e => e.GeneratedAt).Column("generated_at", ESqliteTypeName.INTEGER, "生成时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.ReadAt).Column("read_at", ESqliteTypeName.INTEGER, "首次打开时间（Unix 时间戳，秒）");
        builder.Property(e => e.Backfilled).Column("backfilled", ESqliteTypeName.INTEGER, "补印：不是当时生成的").IsRequired();
        builder.Property(e => e.Payload).Column("payload", ESqliteTypeName.TEXT, "冻结的完整内容（JSON）").IsRequired();
    }
}
