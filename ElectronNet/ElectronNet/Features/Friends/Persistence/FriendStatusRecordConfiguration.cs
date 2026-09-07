using ElectronNet.Enums;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SteamStat.Core.Features.Friends.Persistence;

/// <summary>
/// FriendStatusRecord 表配置
/// </summary>
internal sealed class FriendStatusRecordConfiguration : IEntityTypeConfiguration<FriendStatusRecord>
{
    public void Configure(EntityTypeBuilder<FriendStatusRecord> builder)
    {
        builder.ToTable("friend_status_record", t => t
            .HasComment("好友状态变化记录表")
        );

        builder.HasKey(e => e.Id);

        builder.HasIndex(e => e.AccountName, "friend_status_record_account_name_idx");
        builder.HasIndex(e => e.FriendSteamId, "friend_status_record_friend_steam_id_idx");
        builder.HasIndex(e => e.Timestamp, "friend_status_record_timestamp_idx");
        builder.HasIndex(e => new { e.AccountName, e.FriendSteamId }, "friend_status_record_account_friend_idx");

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
            .HasComment("被记录好友的 Steam ID")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(e => e.FriendPersonaName)
            .HasColumnName("friend_persona_name")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("好友昵称（变化时快照）")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(e => e.ChangeType)
            .HasColumnName("change_type")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("变化类型：state / game / personaName")
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(e => e.PreviousValue)
            .HasColumnName("previous_value")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("变化前的值（JSON 字符串）")
            .HasMaxLength(int.MaxValue);

        builder.Property(e => e.CurrentValue)
            .HasColumnName("current_value")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("变化后的值（JSON 字符串）")
            .HasMaxLength(int.MaxValue);

        builder.Property(e => e.Timestamp)
            .HasColumnName("timestamp")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("变化发生时间（Unix 时间戳，秒）")
            .IsRequired();
    }
}
