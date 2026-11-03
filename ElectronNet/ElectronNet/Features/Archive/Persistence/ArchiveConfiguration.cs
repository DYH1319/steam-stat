using ElectronNet.Enums;
using ElectronNet.Features.Achievements.Persistence;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ElectronNet.Features.Archive.Persistence;

/// <summary>
/// ScreenshotArchive 表配置
/// </summary>
internal sealed class ScreenshotArchiveConfiguration : IEntityTypeConfiguration<ScreenshotArchive>
{
    public void Configure(EntityTypeBuilder<ScreenshotArchive> builder)
    {
        builder.ToTable("screenshot_archive", t => t.HasComment("截图档案表（Steam 截图目录索引）"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.AccountId, e.AppId, e.FileName }, "screenshot_archive_uidx").IsUnique();
        builder.HasIndex(e => e.TakenAt, "screenshot_archive_taken_idx");

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.AccountId).Column("account_id", ESqliteTypeName.INTEGER, "Steam AccountID").IsRequired();
        builder.Property(e => e.SteamId).Column("steam_id", ESqliteTypeName.TEXT, "64 位 SteamID").HasMaxLength(32).IsRequired();
        builder.Property(e => e.AppId).Column("app_id", ESqliteTypeName.INTEGER, "应用 ID").IsRequired();
        builder.Property(e => e.FileName).Column("file_name", ESqliteTypeName.TEXT, "截图文件名").HasMaxLength(260).IsRequired();
        builder.Property(e => e.FilePath).Column("file_path", ESqliteTypeName.TEXT, "截图绝对路径").HasMaxLength(1024).IsRequired();
        builder.Property(e => e.ThumbnailPath).Column("thumbnail_path", ESqliteTypeName.TEXT, "缩略图绝对路径").HasMaxLength(1024);
        builder.Property(e => e.TakenAt).Column("taken_at", ESqliteTypeName.INTEGER, "拍摄时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.FileSize).Column("file_size", ESqliteTypeName.INTEGER, "文件大小（字节）").IsRequired();
        builder.Property(e => e.Width).Column("width", ESqliteTypeName.INTEGER, "宽度（像素）");
        builder.Property(e => e.Height).Column("height", ESqliteTypeName.INTEGER, "高度（像素）");
        builder.Property(e => e.Caption).Column("caption", ESqliteTypeName.TEXT, "截图说明").HasMaxLength(1024);
        builder.Property(e => e.IndexedAt).Column("indexed_at", ESqliteTypeName.INTEGER, "最近一次索引时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.MissingSince).Column("missing_since", ESqliteTypeName.INTEGER, "文件不再存在的首次发现时间（Unix 时间戳，秒）");
    }
}

/// <summary>
/// SaveBackupTarget 表配置
/// </summary>
internal sealed class SaveBackupTargetConfiguration : IEntityTypeConfiguration<SaveBackupTarget>
{
    public void Configure(EntityTypeBuilder<SaveBackupTarget> builder)
    {
        builder.ToTable("save_backup_target", t => t.HasComment("存档备份目录表"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => e.Path, "save_backup_target_path_uidx").IsUnique();
        builder.HasIndex(e => e.AppId, "save_backup_target_app_idx");

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.AppId).Column("app_id", ESqliteTypeName.INTEGER, "应用 ID（0 = 未关联）").IsRequired();
        builder.Property(e => e.Kind).Column("kind", ESqliteTypeName.TEXT, "目录来源（steamCloud/autoCloud/custom）").HasMaxLength(16).IsRequired();
        builder.Property(e => e.Path).Column("path", ESqliteTypeName.TEXT, "目录绝对路径（规范化）").HasMaxLength(1024).IsRequired();
        builder.Property(e => e.SteamId).Column("steam_id", ESqliteTypeName.TEXT, "所属账号 64 位 SteamID").HasMaxLength(32);
        builder.Property(e => e.Label).Column("label", ESqliteTypeName.TEXT, "备注名").HasMaxLength(128);
        builder.Property(e => e.Enabled).Column("enabled", ESqliteTypeName.INTEGER, "是否参与自动快照").IsRequired();
        builder.Property(e => e.CreatedAt).Column("created_at", ESqliteTypeName.INTEGER, "添加时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.LastScanAt).Column("last_scan_at", ESqliteTypeName.INTEGER, "最近一次扫描时间（Unix 时间戳，秒）");
        builder.Property(e => e.LastSnapshotAt).Column("last_snapshot_at", ESqliteTypeName.INTEGER, "最近一次产生版本的时间（Unix 时间戳，秒）");
        builder.Property(e => e.LastError).Column("last_error", ESqliteTypeName.TEXT, "最近一次失败原因").HasMaxLength(64);
    }
}

/// <summary>
/// SaveBackupSnapshot 表配置
/// </summary>
internal sealed class SaveBackupSnapshotConfiguration : IEntityTypeConfiguration<SaveBackupSnapshot>
{
    public void Configure(EntityTypeBuilder<SaveBackupSnapshot> builder)
    {
        builder.ToTable("save_backup_snapshot", t => t.HasComment("存档版本表"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.TargetId, e.CreatedAt }, "save_backup_snapshot_target_idx");

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.TargetId).Column("target_id", ESqliteTypeName.INTEGER, "备份目录 ID").IsRequired();
        builder.Property(e => e.CreatedAt).Column("created_at", ESqliteTypeName.INTEGER, "快照时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.FileCount).Column("file_count", ESqliteTypeName.INTEGER, "文件数").IsRequired();
        builder.Property(e => e.TotalSize).Column("total_size", ESqliteTypeName.INTEGER, "总大小（字节）").IsRequired();
        builder.Property(e => e.AddedSize).Column("added_size", ESqliteTypeName.INTEGER, "新写入备份库的字节数").IsRequired();
        builder.Property(e => e.ChangedFiles).Column("changed_files", ESqliteTypeName.INTEGER, "相比上一版本变化的文件数").IsRequired();
        builder.Property(e => e.Trigger).Column("trigger", ESqliteTypeName.TEXT, "触发方式（manual/schedule/sessionEnd）").HasMaxLength(16).IsRequired();
        builder.Property(e => e.ManifestHash).Column("manifest_hash", ESqliteTypeName.TEXT, "清单指纹").HasMaxLength(64).IsRequired();
        builder.Property(e => e.LatestModifiedAt).Column("latest_modified_at", ESqliteTypeName.INTEGER, "最近修改的文件时间（Unix 时间戳，秒）");
    }
}

/// <summary>
/// SaveBackupFile 表配置
/// </summary>
internal sealed class SaveBackupFileConfiguration : IEntityTypeConfiguration<SaveBackupFile>
{
    public void Configure(EntityTypeBuilder<SaveBackupFile> builder)
    {
        builder.ToTable("save_backup_file", t => t.HasComment("存档版本文件清单表"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => e.SnapshotId, "save_backup_file_snapshot_idx");
        builder.HasIndex(e => e.Sha256, "save_backup_file_sha_idx");

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.SnapshotId).Column("snapshot_id", ESqliteTypeName.INTEGER, "版本 ID").IsRequired();
        builder.Property(e => e.RelativePath).Column("relative_path", ESqliteTypeName.TEXT, "相对路径（/ 分隔）").HasMaxLength(1024).IsRequired();
        builder.Property(e => e.Size).Column("size", ESqliteTypeName.INTEGER, "文件大小（字节）").IsRequired();
        builder.Property(e => e.ModifiedAt).Column("modified_at", ESqliteTypeName.INTEGER, "文件修改时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.Sha256).Column("sha256", ESqliteTypeName.TEXT, "内容 SHA-256（小写十六进制）").HasMaxLength(64).IsRequired();
    }
}

/// <summary>
/// ProfileHistoryEntry 表配置
/// </summary>
internal sealed class ProfileHistoryEntryConfiguration : IEntityTypeConfiguration<ProfileHistoryEntry>
{
    public void Configure(EntityTypeBuilder<ProfileHistoryEntry> builder)
    {
        builder.ToTable("profile_history", t => t.HasComment("自己的资料变化记录表（昵称/头像/等级/展示徽章）"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.SteamId, e.Field, e.ObservedAt }, "profile_history_idx");

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.SteamId).Column("steam_id", ESqliteTypeName.TEXT, "64 位 SteamID").HasMaxLength(32).IsRequired();
        builder.Property(e => e.Field).Column("field", ESqliteTypeName.TEXT, "字段（personaName/avatar/level/favoriteBadge）").HasMaxLength(32).IsRequired();
        builder.Property(e => e.Value).Column("value", ESqliteTypeName.TEXT, "新值").HasMaxLength(1024).IsRequired();
        builder.Property(e => e.PreviousValue).Column("previous_value", ESqliteTypeName.TEXT, "旧值（首次观测为空）").HasMaxLength(1024);
        builder.Property(e => e.ObservedAt).Column("observed_at", ESqliteTypeName.INTEGER, "观测时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.Source).Column("source", ESqliteTypeName.TEXT, "来源（cm/friends/local）").HasMaxLength(16).IsRequired();
        builder.Property(e => e.LocalPath).Column("local_path", ESqliteTypeName.TEXT, "头像本地存档路径").HasMaxLength(1024);
    }
}
