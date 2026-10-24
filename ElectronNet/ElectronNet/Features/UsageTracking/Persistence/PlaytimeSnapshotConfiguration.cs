using ElectronNet.Enums;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SteamStat.Core.Features.UsageTracking.Persistence;

/// <summary>
/// PlaytimeSnapshot 实体配置
/// </summary>
internal sealed class PlaytimeSnapshotConfiguration : IEntityTypeConfiguration<PlaytimeSnapshot>
{
    public void Configure(EntityTypeBuilder<PlaytimeSnapshot> builder)
    {
        builder.ToTable("playtime_snapshot", t => t
            .HasComment("Steam 官方累计时长快照（localconfig / CM），用于历史导入与对账")
        );

        builder.HasKey(e => e.Id);

        builder.HasIndex(
            e => new { e.SteamId, e.AppId, e.Source, e.CapturedAt },
            "playtime_snapshot_lookup_idx");

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

        builder.Property(e => e.AppId)
            .HasColumnName("app_id")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("App ID")
            .IsRequired();

        builder.Property(e => e.Source)
            .HasColumnName("source")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("快照来源（localconfig | cm）")
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(e => e.PlaytimeMinutes)
            .HasColumnName("playtime_minutes")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("Steam 官方累计时长（分钟）")
            .IsRequired();

        builder.Property(e => e.CapturedAt)
            .HasColumnName("captured_at")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("首次观察到该值的 Unix 时间戳")
            .IsRequired();

        builder.Property(e => e.LastSeenAt)
            .HasColumnName("last_seen_at")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("最后一次仍观察到该值未变化的 Unix 时间戳")
            .IsRequired();

        builder.Property(e => e.LastPlayed)
            .HasColumnName("last_played")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("官方最后游玩 Unix 时间戳（0 存为 null）");

        builder.Property(e => e.IsBaseline)
            .HasColumnName("is_baseline")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("是否为该组对账基线")
            .HasDefaultValue(false)
            .IsRequired();
    }
}
