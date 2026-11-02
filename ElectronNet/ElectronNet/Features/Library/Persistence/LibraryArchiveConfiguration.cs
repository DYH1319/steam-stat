using ElectronNet.Enums;
using ElectronNet.Features.Achievements.Persistence;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ElectronNet.Features.Library.Persistence;

/// <summary>
/// LibraryAcquisition 表配置
/// </summary>
internal sealed class LibraryAcquisitionConfiguration : IEntityTypeConfiguration<LibraryAcquisition>
{
    public void Configure(EntityTypeBuilder<LibraryAcquisition> builder)
    {
        builder.ToTable("library_acquisition", t => t.HasComment("游戏入库时间表（许可证 / 首次观测）"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.AccountName, e.PackageId, e.AppId }, "library_acquisition_uidx").IsUnique();
        builder.HasIndex(e => new { e.AccountName, e.AppId }, "library_acquisition_app_idx");

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.AccountName).Column("account_name", ESqliteTypeName.TEXT, "登录用户账户名").HasMaxLength(256).IsRequired();
        builder.Property(e => e.AppId).Column("app_id", ESqliteTypeName.INTEGER, "应用 ID（0 = 无应用的 package 占位）").IsRequired();
        builder.Property(e => e.PackageId).Column("package_id", ESqliteTypeName.INTEGER, "许可证 package ID（0 = 首次观测）").IsRequired();
        builder.Property(e => e.AcquiredAt).Column("acquired_at", ESqliteTypeName.INTEGER, "入库时间（Unix 时间戳，秒）").IsRequired();
    }
}

/// <summary>
/// LibraryGameMeta 表配置
/// </summary>
internal sealed class LibraryGameMetaConfiguration : IEntityTypeConfiguration<LibraryGameMeta>
{
    public void Configure(EntityTypeBuilder<LibraryGameMeta> builder)
    {
        builder.ToTable("library_game_meta", t => t.HasComment("游戏档案信息表（状态 / 评分 / 价格）"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.AccountName, e.AppId }, "library_game_meta_uidx").IsUnique();

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.AccountName).Column("account_name", ESqliteTypeName.TEXT, "登录用户账户名").HasMaxLength(256).IsRequired();
        builder.Property(e => e.AppId).Column("app_id", ESqliteTypeName.INTEGER, "应用 ID").IsRequired();
        builder.Property(e => e.Status).Column("status", ESqliteTypeName.TEXT, "游戏状态（playing/completed/abandoned/wantToPlay/shelved）").HasMaxLength(32);
        builder.Property(e => e.CompletedAt).Column("completed_at", ESqliteTypeName.INTEGER, "通关时间（Unix 时间戳，秒）");
        builder.Property(e => e.Rating).Column("rating", ESqliteTypeName.INTEGER, "个人评分（1~10）");
        builder.Property(e => e.Price).Column("price", ESqliteTypeName.REAL, "购买价格");
        builder.Property(e => e.Currency).Column("currency", ESqliteTypeName.TEXT, "价格币种").HasMaxLength(8);
        builder.Property(e => e.Note).Column("note", ESqliteTypeName.TEXT, "备注").HasMaxLength(2000);
        builder.Property(e => e.UpdatedAt).Column("updated_at", ESqliteTypeName.INTEGER, "更新时间（Unix 时间戳，秒）").IsRequired();
    }
}
