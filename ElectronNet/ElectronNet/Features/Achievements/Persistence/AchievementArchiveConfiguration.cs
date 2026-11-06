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
        builder.Property(e => e.GroupId).Column("group_id", ESqliteTypeName.INTEGER, "所在成就集 ID");
        builder.Property(e => e.Archived).Column("archived", ESqliteTypeName.INTEGER, "成就或所在的集是否已归档").IsRequired().HasDefaultValue(false);
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
        builder.Property(e => e.ProgressTotal).Column("progress_total", ESqliteTypeName.INTEGER, "归档时概览成就总数");
        builder.Property(e => e.ProgressUnlocked).Column("progress_unlocked", ESqliteTypeName.INTEGER, "归档时概览已解锁数");
        builder.Property(e => e.AllTotal).Column("all_total", ESqliteTypeName.INTEGER, "全部成就数，含未拥有的 DLC 和归档成就");
        builder.Property(e => e.AllUnlocked).Column("all_unlocked", ESqliteTypeName.INTEGER, "全部成就里的已解锁数");
        builder.Property(e => e.SyncedAt).Column("synced_at", ESqliteTypeName.INTEGER, "最近归档时间（Unix 时间戳，秒）").IsRequired();
    }
}

/// <summary>
/// AchievementSetArchive 表配置
/// </summary>
internal sealed class AchievementSetArchiveConfiguration : IEntityTypeConfiguration<AchievementSetArchive>
{
    public void Configure(EntityTypeBuilder<AchievementSetArchive> builder)
    {
        builder.ToTable("achievement_set_archive", t => t.HasComment("成就集进度归档表"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.AccountName, e.AppId, e.GroupId }, "achievement_set_archive_uidx").IsUnique();

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.AccountName).Column("account_name", ESqliteTypeName.TEXT, "登录用户账户名").HasMaxLength(256).IsRequired();
        builder.Property(e => e.SteamId).Column("steam_id", ESqliteTypeName.TEXT, "账号 Steam ID").HasMaxLength(64).IsRequired();
        builder.Property(e => e.AppId).Column("app_id", ESqliteTypeName.INTEGER, "应用 ID").IsRequired();
        builder.Property(e => e.GroupId).Column("group_id", ESqliteTypeName.INTEGER, "成就集 ID").IsRequired();
        builder.Property(e => e.Name).Column("name", ESqliteTypeName.TEXT, "成就集本地化名称，基础集为空").HasMaxLength(512).IsRequired();
        builder.Property(e => e.DlcAppId).Column("dlc_app_id", ESqliteTypeName.INTEGER, "DLC 应用 ID");
        builder.Property(e => e.Archived).Column("archived", ESqliteTypeName.INTEGER, "成就集是否已归档").IsRequired();
        builder.Property(e => e.Counted).Column("counted", ESqliteTypeName.INTEGER, "是否计入完成度").IsRequired();
        builder.Property(e => e.Owned).Column("owned", ESqliteTypeName.INTEGER, "DLC 是否拥有，基础集和无法判断时为空");
        builder.Property(e => e.Total).Column("total", ESqliteTypeName.INTEGER, "对这名玩家的成就数，不含未获得的归档成就").IsRequired();
        builder.Property(e => e.Unlocked).Column("unlocked", ESqliteTypeName.INTEGER, "已解锁数").IsRequired();
        builder.Property(e => e.AllTotal).Column("all_total", ESqliteTypeName.INTEGER, "集内全部成就数").IsRequired();
        builder.Property(e => e.Completed).Column("completed", ESqliteTypeName.INTEGER, "是否已完成，完成后不再清除").IsRequired();
        builder.Property(e => e.CompletedAt).Column("completed_at", ESqliteTypeName.INTEGER, "完成时间，取集内最后一个解锁时间（Unix 时间戳，秒）");
        builder.Property(e => e.SyncedAt).Column("synced_at", ESqliteTypeName.INTEGER, "最近归档时间（Unix 时间戳，秒）").IsRequired();
    }
}

/// <summary>
/// AchievementSchemaState 表配置
/// </summary>
internal sealed class AchievementSchemaStateConfiguration : IEntityTypeConfiguration<AchievementSchemaState>
{
    public void Configure(EntityTypeBuilder<AchievementSchemaState> builder)
    {
        builder.ToTable("achievement_schema_state", t => t.HasComment("最近一次观测到的成就 schema"));
        builder.HasKey(e => e.AppId);

        builder.Property(e => e.AppId).Column("app_id", ESqliteTypeName.INTEGER, "应用 ID").IsRequired().ValueGeneratedNever();
        builder.Property(e => e.SchemaHash).Column("schema_hash", ESqliteTypeName.INTEGER, "schema 哈希").IsRequired();
        builder.Property(e => e.Language).Column("language", ESqliteTypeName.TEXT, "成就名称的语言").HasMaxLength(64).IsRequired();
        builder.Property(e => e.Items).Column("items", ESqliteTypeName.TEXT, "每个成就的状态（JSON）").IsRequired();
        builder.Property(e => e.ObservedAt).Column("observed_at", ESqliteTypeName.INTEGER, "观测时间（Unix 时间戳，秒）").IsRequired();
    }
}

/// <summary>
/// AchievementSchemaChange 表配置
/// </summary>
internal sealed class AchievementSchemaChangeConfiguration : IEntityTypeConfiguration<AchievementSchemaChange>
{
    public void Configure(EntityTypeBuilder<AchievementSchemaChange> builder)
    {
        builder.ToTable("achievement_schema_change", t => t.HasComment("成就 schema 变更史"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.AppId, e.InternalName, e.Kind, e.SchemaHash }, "achievement_schema_change_uidx").IsUnique();

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.AppId).Column("app_id", ESqliteTypeName.INTEGER, "应用 ID").IsRequired();
        builder.Property(e => e.SchemaHash).Column("schema_hash", ESqliteTypeName.INTEGER, "变化后的 schema 哈希").IsRequired();
        builder.Property(e => e.InternalName).Column("internal_name", ESqliteTypeName.TEXT, "成就内部名称").HasMaxLength(256).IsRequired();
        builder.Property(e => e.Name).Column("name", ESqliteTypeName.TEXT, "成就本地化名称").HasMaxLength(512).IsRequired();
        builder.Property(e => e.Kind).Column("kind", ESqliteTypeName.TEXT, "变化类型").HasMaxLength(16).IsRequired();
        builder.Property(e => e.OldValue).Column("old_value", ESqliteTypeName.TEXT, "旧值（集名或成就名）").HasMaxLength(512);
        builder.Property(e => e.NewValue).Column("new_value", ESqliteTypeName.TEXT, "新值（集名或成就名）").HasMaxLength(512);
        builder.Property(e => e.DetectedAt).Column("detected_at", ESqliteTypeName.INTEGER, "发现时间（Unix 时间戳，秒）").IsRequired();
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
