using ElectronNet.Enums;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ElectronNet.Features.Friends.Persistence;

internal sealed class RichPresenceRecordConfiguration : IEntityTypeConfiguration<RichPresenceRecord>
{
    public void Configure(EntityTypeBuilder<RichPresenceRecord> builder)
    {
        builder.ToTable("rich_presence_record", t => t.HasComment("游戏内状态变化记录，保留原始键值"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.SteamId, e.ObservedAt }, "rich_presence_record_steam_id_observed_at_idx");
        builder.HasIndex(e => new { e.AppId, e.ObservedAt }, "rich_presence_record_app_id_observed_at_idx");

        builder.Property(e => e.Id).HasColumnName("id").HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.AccountName).HasColumnName("account_name").HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("观察到这条状态的本机账号").HasMaxLength(128).IsRequired();
        builder.Property(e => e.SteamId).HasColumnName("steam_id").HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("状态所属的玩家").HasMaxLength(32).IsRequired();
        builder.Property(e => e.IsSelf).HasColumnName("is_self").HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("是否账号本人").IsRequired();
        builder.Property(e => e.AppId).HasColumnName("app_id").HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("应用 ID").IsRequired();
        builder.Property(e => e.RawValues).HasColumnName("raw_values").HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("Steam 给出的原始键值（JSON）").IsRequired();
        builder.Property(e => e.DisplayText).HasColumnName("display_text").HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("解析后的状态文案");
        builder.Property(e => e.Source).HasColumnName("source").HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("来源：push / poll").HasMaxLength(16).IsRequired();
        builder.Property(e => e.ObservedAt).HasColumnName("observed_at").HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("观测时间的 Unix 时间戳").IsRequired();
    }
}
