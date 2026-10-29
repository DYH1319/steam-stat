using ElectronNet.Enums;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SteamStat.Core.Features.Friends.Persistence;

/// <summary>
/// FriendTracking 表配置
/// </summary>
internal sealed class FriendTrackingConfiguration : IEntityTypeConfiguration<FriendTracking>
{
    public void Configure(EntityTypeBuilder<FriendTracking> builder)
    {
        builder.ToTable("friend_tracking", t => t
            .HasComment("好友追踪名单表")
        );

        builder.HasKey(e => e.Id);

        builder.HasIndex(e => new { e.AccountName, e.FriendSteamId }, "friend_tracking_account_friend_uidx")
            .IsUnique();

        builder.Property(e => e.Id)
            .HasColumnName("id")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("ID")
            .IsRequired()
            .ValueGeneratedOnAdd();

        builder.Property(e => e.AccountName)
            .HasColumnName("account_name")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("登录用户账户名")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(e => e.FriendSteamId)
            .HasColumnName("friend_steam_id")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("被追踪好友的 Steam ID")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(e => e.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("开始追踪时间（Unix 时间戳，秒）")
            .IsRequired();
    }
}
