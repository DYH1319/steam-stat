using ElectronNet.Enums;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SteamStat.Core.Features.UsageTracking.Persistence;

/// <summary>
/// SessionImportBatch 实体配置
/// </summary>
internal sealed class SessionImportBatchConfiguration : IEntityTypeConfiguration<SessionImportBatch>
{
    public void Configure(EntityTypeBuilder<SessionImportBatch> builder)
    {
        builder.ToTable("session_import_batch", t => t
            .HasComment("CSV 会话导入批次")
        );

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .HasColumnName("id")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("批次 ID（Guid，32 位无连字符）")
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(e => e.FileName)
            .HasColumnName("file_name")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("导入文件名")
            .HasMaxLength(260)
            .IsRequired();

        builder.Property(e => e.ImportedAt)
            .HasColumnName("imported_at")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("导入的 Unix 时间戳")
            .IsRequired();

        builder.Property(e => e.RowCount)
            .HasColumnName("row_count")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("导入的会话条数")
            .IsRequired();
    }
}
