using ElectronNet.Enums;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SteamStat.Core.Features.UsageTracking.Persistence;

/// <summary>
/// UseAppRecord 表配置
/// </summary>
internal sealed class UseAppRecordConfiguration : IEntityTypeConfiguration<UseAppRecord>
{
    public void Configure(EntityTypeBuilder<UseAppRecord> builder)
    {
        builder.ToTable("use_app_record", t => t
            .HasComment("应用使用记录表")
        );

        builder.HasKey(e => e.Id);

        builder.HasIndex(e => e.AppId, "use_app_record_app_id_idx");
        builder.HasIndex(e => e.SteamId, "use_app_record_steam_id_idx");
        builder.HasIndex(e => e.StartTime, "use_app_record_start_time_idx");
        builder.HasIndex(e => new { e.SteamId, e.AppId }, "use_app_record_steam_id_app_id_idx");

        builder.Property(e => e.Id)
            .HasColumnName("id")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("ID")
            .IsRequired()
            .ValueGeneratedOnAdd();

        builder.Property(e => e.AppId)
            .HasColumnName("app_id")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("使用的 App ID")
            .IsRequired();

        builder.Property(e => e.SteamId)
            .HasColumnName("steam_id")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("使用 App 的 Steam ID")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(e => e.StartTime)
            .HasColumnName("start_time")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("开始使用的 Unix 时间戳")
            .IsRequired();

        builder.Property(e => e.EndTime)
            .HasColumnName("end_time")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("结束使用的 Unix 时间戳");

        builder.Property(e => e.Duration)
            .HasColumnName("duration")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("持续使用时间");
    }
}
