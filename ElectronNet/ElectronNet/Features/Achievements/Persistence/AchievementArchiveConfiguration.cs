using ElectronNet.Enums;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ElectronNet.Features.Achievements.Persistence;

internal static class ArchiveColumns
{
    internal static PropertyBuilder<T> Column<T>(
        this PropertyBuilder<T> builder, string name, ESqliteTypeName type, string comment)
        => builder.HasColumnName(name).HasColumnType(type.ToString()).HasComment(comment);
}

/// <summary>
/// AchievementUnlockArchive 表配置
/// </summary>
internal sealed class AchievementUnlockArchiveConfiguration : IEntityTypeConfiguration<AchievementUnlockArchive>
{
    public void Configure(EntityTypeBuilder<AchievementUnlockArchive> builder)
    {
        builder.ToTable("achievement_unlock_archive", t => t.HasComment("已解锁成就永久归档表"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.AccountName, e.AppId, e.InternalName }, "achievement_unlock_archive_uidx").IsUnique();
        builder.HasIndex(e => new { e.AccountName, e.UnlockTime }, "achievement_unlock_archive_time_idx");

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.AccountName).Column("account_name", ESqliteTypeName.TEXT, "登录用户账户名").HasMaxLength(256).IsRequired();
        builder.Property(e => e.SteamId).Column("steam_id", ESqliteTypeName.TEXT, "账号 Steam ID").HasMaxLength(64).IsRequired();
        builder.Property(e => e.AppId).Column("app_id", ESqliteTypeName.INTEGER, "应用 ID").IsRequired();
        builder.Property(e => e.InternalName).Column("internal_name", ESqliteTypeName.TEXT, "成就内部名称").HasMaxLength(256).IsRequired();
        builder.Property(e => e.Name).Column("name", ESqliteTypeName.TEXT, "成就本地化名称").HasMaxLength(512).IsRequired();
        builder.Property(e => e.Description).Column("description", ESqliteTypeName.TEXT, "成就本地化描述").HasMaxLength(2048).IsRequired();
        builder.Property(e => e.Icon).Column("icon", ESqliteTypeName.TEXT, "成就图标 URL").HasMaxLength(1024).IsRequired();
        builder.Property(e => e.Hidden).Column("hidden", ESqliteTypeName.INTEGER, "是否隐藏成就").IsRequired();
        builder.Property(e => e.GlobalPercent).Column("global_percent", ESqliteTypeName.REAL, "全球解锁率（%）");
        builder.Property(e => e.UnlockTime).Column("unlock_time", ESqliteTypeName.INTEGER, "解锁时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.RecordedAt).Column("recorded_at", ESqliteTypeName.INTEGER, "首次归档时间（Unix 时间戳，秒）").IsRequired();
    }
}

/// <summary>
/// AchievementGameArchive 表配置
/// </summary>
internal sealed class AchievementGameArchiveConfiguration : IEntityTypeConfiguration<AchievementGameArchive>
{
    public void Configure(EntityTypeBuilder<AchievementGameArchive> builder)
    {
        builder.ToTable("achievement_game_archive", t => t.HasComment("游戏成就进度归档表"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.AccountName, e.AppId }, "achievement_game_archive_uidx").IsUnique();

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.AccountName).Column("account_name", ESqliteTypeName.TEXT, "登录用户账户名").HasMaxLength(256).IsRequired();
        builder.Property(e => e.SteamId).Column("steam_id", ESqliteTypeName.TEXT, "账号 Steam ID").HasMaxLength(64).IsRequired();
        builder.Property(e => e.AppId).Column("app_id", ESqliteTypeName.INTEGER, "应用 ID").IsRequired();
        builder.Property(e => e.AppName).Column("app_name", ESqliteTypeName.TEXT, "应用名称").HasMaxLength(512).IsRequired();
        builder.Property(e => e.Total).Column("total", ESqliteTypeName.INTEGER, "成就总数").IsRequired();
        builder.Property(e => e.Unlocked).Column("unlocked", ESqliteTypeName.INTEGER, "已解锁数").IsRequired();
        builder.Property(e => e.SyncedAt).Column("synced_at", ESqliteTypeName.INTEGER, "最近归档时间（Unix 时间戳，秒）").IsRequired();
    }
}

/// <summary>
/// AchievementTarget 表配置
/// </summary>
internal sealed class AchievementTargetConfiguration : IEntityTypeConfiguration<AchievementTarget>
{
    public void Configure(EntityTypeBuilder<AchievementTarget> builder)
    {
        builder.ToTable("achievement_target", t => t.HasComment("成就目标清单表"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.AccountName, e.AppId, e.InternalName }, "achievement_target_uidx").IsUnique();

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.AccountName).Column("account_name", ESqliteTypeName.TEXT, "登录用户账户名").HasMaxLength(256).IsRequired();
        builder.Property(e => e.AppId).Column("app_id", ESqliteTypeName.INTEGER, "应用 ID").IsRequired();
        builder.Property(e => e.AppName).Column("app_name", ESqliteTypeName.TEXT, "应用名称").HasMaxLength(512).IsRequired();
        builder.Property(e => e.InternalName).Column("internal_name", ESqliteTypeName.TEXT, "成就内部名称").HasMaxLength(256).IsRequired();
        builder.Property(e => e.Name).Column("name", ESqliteTypeName.TEXT, "成就名称").HasMaxLength(512).IsRequired();
        builder.Property(e => e.Description).Column("description", ESqliteTypeName.TEXT, "成就描述").HasMaxLength(2048).IsRequired();
        builder.Property(e => e.Icon).Column("icon", ESqliteTypeName.TEXT, "成就图标 URL").HasMaxLength(1024).IsRequired();
        builder.Property(e => e.Note).Column("note", ESqliteTypeName.TEXT, "备注").HasMaxLength(1024);
        builder.Property(e => e.CreatedAt).Column("created_at", ESqliteTypeName.INTEGER, "加入清单时间（Unix 时间戳，秒）").IsRequired();
    }
}
