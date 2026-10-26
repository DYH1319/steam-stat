using ElectronNet.Enums;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ElectronNet.Features.UsageTracking.Persistence;

/// <summary>
/// Device 表配置
/// </summary>
internal sealed class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
    public void Configure(EntityTypeBuilder<Device> builder)
    {
        builder.ToTable("device", t => t
            .HasComment("采集设备表")
        );

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .HasColumnName("id")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("设备 ID（Guid）")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(e => e.Name)
            .HasColumnName("name")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("设备名称")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(e => e.IsCurrent)
            .HasColumnName("is_current")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("是否为当前设备")
            .IsRequired();

        builder.Property(e => e.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("创建时间的 Unix 时间戳")
            .IsRequired();
    }
}
