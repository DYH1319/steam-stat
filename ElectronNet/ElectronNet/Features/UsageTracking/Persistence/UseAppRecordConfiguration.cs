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

        builder.HasIndex(e => e.DeviceId, "use_app_record_device_id_idx");

        builder.Property(e => e.LastHeartbeat)
            .HasColumnName("last_heartbeat")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("最近一次心跳的 Unix 时间戳");

        builder.Property(e => e.IdleDuration)
            .HasColumnName("idle_duration")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("离开（AFK）时长，秒")
            .HasDefaultValue(0L)
            .IsRequired();

        builder.Property(e => e.ForegroundDuration)
            .HasColumnName("foreground_duration")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("前台时长，秒（null 表示未知）");

        builder.Property(e => e.DeviceId)
            .HasColumnName("device_id")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("采集设备 ID")
            .HasMaxLength(64);

        builder.Property(e => e.Source)
            .HasColumnName("source")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("记录来源（auto | merged）")
            .HasMaxLength(16)
            .HasDefaultValue("auto")
            .IsRequired();

        builder.Property(e => e.EndReason)
            .HasColumnName("end_reason")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("结束原因（exited | suspended | recovered | ended | discarded）")
            .HasMaxLength(16);

        builder.Property(e => e.Note)
            .HasColumnName("note")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("用户备注")
            .HasMaxLength(2000);

        builder.Property(e => e.Tags)
            .HasColumnName("tags")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("标签（JSON 字符串数组）");

        builder.Property(e => e.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("最近一次手动编辑/合并的 Unix 时间戳");
    }
}
