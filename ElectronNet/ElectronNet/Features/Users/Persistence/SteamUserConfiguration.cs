using ElectronNet.Enums;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SteamStat.Core.Features.Users.Persistence;

/// <summary>
/// SteamUser 表配置
/// </summary>
internal sealed class SteamUserConfiguration : IEntityTypeConfiguration<SteamUser>
{
    public void Configure(EntityTypeBuilder<SteamUser> builder)
    {
        builder.ToTable("steam_user", t => t
            .HasComment("Steam 用户表")
        );

        builder.HasKey(e => e.Id);

        builder.HasIndex(e => e.SteamId).IsUnique();
        builder.HasIndex(e => e.AccountId).IsUnique();

        builder.Property(e => e.Id)
            .HasColumnName("id")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("ID")
            .IsRequired()
            .ValueGeneratedOnAdd();

        builder.Property(e => e.SteamId)
            .HasColumnName("steam_id")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("Steam ID")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(e => e.AccountId)
            .HasColumnName("account_id")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("Account ID")
            .IsRequired();

        builder.Property(e => e.AccountName)
            .HasColumnName("account_name")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("账号名")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(e => e.PersonaName)
            .HasColumnName("persona_name")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("昵称")
            .HasMaxLength(256);

        builder.Property(e => e.RememberPassword)
            .HasColumnName("remember_password")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("是否记住密码");

        builder.Property(e => e.WantsOfflineMode)
            .HasColumnName("wants_offline_mode")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("是否开启离线模式");

        builder.Property(e => e.SkipOfflineModeWarning)
            .HasColumnName("skip_offline_mode_warning")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("是否跳过离线模式警告");

        builder.Property(e => e.AllowAutoLogin)
            .HasColumnName("allow_auto_login")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("是否允许自动登录");

        builder.Property(e => e.MostRecent)
            .HasColumnName("most_recent")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("是否最近登录");

        builder.Property(e => e.Timestamp)
            .HasColumnName("timestamp")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("最近登录时间");

        builder.Property(e => e.AvatarFull)
            .HasColumnName("avatar_full")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("全尺寸头像（184x184）Base64")
            .HasMaxLength(int.MaxValue);

        builder.Property(e => e.AvatarMedium)
            .HasColumnName("avatar_medium")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("中等尺寸头像（64x64）Base64")
            .HasMaxLength(int.MaxValue);

        builder.Property(e => e.AvatarSmall)
            .HasColumnName("avatar_small")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("小尺寸头像（32x32）Base64")
            .HasMaxLength(int.MaxValue);

        builder.Property(e => e.AnimatedAvatar)
            .HasColumnName("animated_avatar")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("动画头像 Base64")
            .HasMaxLength(int.MaxValue);

        builder.Property(e => e.AvatarFrame)
            .HasColumnName("avatar_frame")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("头像边框 Base64")
            .HasMaxLength(int.MaxValue);

        builder.Property(e => e.Level)
            .HasColumnName("level")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("等级");

        builder.Property(e => e.LevelClass)
            .HasColumnName("level_class")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("等级样式类")
            .HasMaxLength(256);
    }
}
