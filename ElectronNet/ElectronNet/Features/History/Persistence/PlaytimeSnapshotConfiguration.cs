using ElectronNet.Enums;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ElectronNet.Features.History.Persistence;

/// <summary>
/// PlaytimeSnapshot 表配置
/// </summary>
internal sealed class PlaytimeSnapshotConfiguration : IEntityTypeConfiguration<PlaytimeSnapshot>
{
    public void Configure(EntityTypeBuilder<PlaytimeSnapshot> builder)
    {
        builder.ToTable("playtime_snapshot", t => t
            .HasComment("Steam 官方累计时长快照表（按值变化分段）")
        );

        builder.HasKey(e => e.Id);

        builder.HasIndex(e => new { e.SteamId, e.AppId, e.FirstObservedAt }, "playtime_snapshot_steam_id_app_id_first_idx");

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

        builder.Property(e => e.PlaytimeMinutes)
            .HasColumnName("playtime_minutes")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("Steam 官方累计时长，分钟")
            .IsRequired();

        builder.Property(e => e.Source)
            .HasColumnName("source")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("数据来源（localconfig | cm）")
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(e => e.FirstObservedAt)
            .HasColumnName("first_observed_at")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("首次观测到该值的 Unix 时间戳")
            .IsRequired();

        builder.Property(e => e.LastObservedAt)
            .HasColumnName("last_observed_at")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("最近一次观测到该值的 Unix 时间戳")
            .IsRequired();

        builder.Property(e => e.LastPlayedAt)
            .HasColumnName("last_played_at")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("Steam 记录的最后游玩 Unix 时间戳");
    }
}
