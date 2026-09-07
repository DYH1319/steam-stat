using ElectronNet.Enums;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SteamStat.Core.Features.Apps.Persistence;

/// <summary>
/// SteamApp 表配置
/// </summary>
internal sealed class SteamAppConfiguration : IEntityTypeConfiguration<SteamApp>
{
    public void Configure(EntityTypeBuilder<SteamApp> builder)
    {
        builder.ToTable("steam_app", t => t
            .HasComment("Steam 应用表")
        );

        builder.HasKey(e => e.Id);

        builder.HasIndex(e => e.AppId).IsUnique();
        builder.HasIndex(e => e.Name, "steam_app_name_idx");
        builder.HasIndex(e => e.Installed, "steam_app_installed_idx");

        builder.Property(e => e.Id)
            .HasColumnName("id")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("ID")
            .IsRequired()
            .ValueGeneratedOnAdd();

        builder.Property(e => e.AppId)
            .HasColumnName("app_id")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("App ID")
            .IsRequired();

        builder.Property(e => e.Name)
            .HasColumnName("name")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("应用名称")
            .HasMaxLength(1024);

        builder.Property(e => e.NameLocalizedJson)
            .HasColumnName("name_localized")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("应用本地化名称 JSON 对象")
            .HasMaxLength(int.MaxValue)
            .IsRequired()
            .HasDefaultValue("{}");

        builder.Property(e => e.Installed)
            .HasColumnName("installed")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("是否已本地安装")
            .IsRequired();

        builder.Property(e => e.InstallDir)
            .HasColumnName("install_dir")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("本地安装目录名称")
            .HasMaxLength(int.MaxValue);

        builder.Property(e => e.InstallDirPath)
            .HasColumnName("install_dir_path")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("本地安装目录绝对路径")
            .HasMaxLength(int.MaxValue);

        builder.Property(e => e.AppOnDisk)
            .HasColumnName("app_on_disk")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("应用文件占用大小");

        builder.Property(e => e.AppOnDiskReal)
            .HasColumnName("app_on_disk_real")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("应用文件真实占用大小");

        builder.Property(e => e.IsRunning)
            .HasColumnName("is_running")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("是否正在运行")
            .IsRequired()
            .HasDefaultValue(false);

        builder.Property(e => e.Type)
            .HasColumnName("type")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("应用类型")
            .HasMaxLength(128);

        builder.Property(e => e.Developer)
            .HasColumnName("developer")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("开发者")
            .HasMaxLength(256);

        builder.Property(e => e.Publisher)
            .HasColumnName("publisher")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("发布者")
            .HasMaxLength(256);

        builder.Property(e => e.SteamReleaseDate)
            .HasColumnName("steam_release_date")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("发布日期");

        builder.Property(e => e.IsFreeApp)
            .HasColumnName("is_free_app")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("是否是免费应用");
    }
}
