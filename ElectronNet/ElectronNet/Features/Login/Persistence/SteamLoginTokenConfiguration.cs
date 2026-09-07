using ElectronNet.Enums;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SteamStat.Core.Features.Login.Persistence;

/// <summary>
/// SteamLoginToken 表配置
/// </summary>
internal sealed class SteamLoginTokenConfiguration : IEntityTypeConfiguration<SteamLoginToken>
{
    public void Configure(EntityTypeBuilder<SteamLoginToken> builder)
    {
        builder.ToTable("steam_login_token", t => t
            .HasComment("Steam 登录 Token 表")
        );

        builder.HasKey(e => e.Id);

        builder.HasIndex(e => e.AccountName).IsUnique();

        builder.Property(e => e.Id)
            .HasColumnName("id")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("ID")
            .IsRequired()
            .ValueGeneratedOnAdd();

        builder.Property(e => e.AccountName)
            .HasColumnName("account_name")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("账号名")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(e => e.AccessToken)
            .HasColumnName("access_token")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("Access Token (JWT)")
            .HasMaxLength(int.MaxValue)
            .IsRequired();

        builder.Property(e => e.RefreshToken)
            .HasColumnName("refresh_token")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("Refresh Token (JWT)")
            .HasMaxLength(int.MaxValue)
            .IsRequired();

        builder.Property(e => e.GuardData)
            .HasColumnName("guard_data")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("Steam Guard 数据")
            .HasMaxLength(int.MaxValue);

        builder.Property(e => e.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("创建时间 Unix 时间戳")
            .IsRequired();
    }
}
