using ElectronNet.Enums;
using ElectronNet.Features.Achievements.Persistence;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ElectronNet.Features.Sync.Persistence;

internal sealed class SyncControlConfiguration : IEntityTypeConfiguration<SyncControl>
{
    public void Configure(EntityTypeBuilder<SyncControl> builder)
    {
        builder.ToTable("sync_control", t => t.HasComment("多设备同步的本机状态（单行）"));
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "固定为 1").IsRequired().ValueGeneratedNever();
        builder.Property(e => e.Enabled).Column("enabled", ESqliteTypeName.INTEGER, "是否启用同步（触发器据此记录变更）").IsRequired();
        builder.Property(e => e.Applying).Column("applying", ESqliteTypeName.INTEGER, "正在应用远端变更（触发器据此跳过）").IsRequired();
        builder.Property(e => e.VaultId).Column("vault_id", ESqliteTypeName.TEXT, "远端保险库 ID").HasMaxLength(64);
        builder.Property(e => e.DeviceSeq).Column("device_seq", ESqliteTypeName.INTEGER, "本机已写出的最大段序号").IsRequired();
        builder.Property(e => e.FullExportDone).Column("full_export_done", ESqliteTypeName.INTEGER, "本机全量快照是否已上传").IsRequired();
        builder.Property(e => e.LastSyncAt).Column("last_sync_at", ESqliteTypeName.INTEGER, "最近一次成功同步（Unix 秒）");
        builder.Property(e => e.LastError).Column("last_error", ESqliteTypeName.TEXT, "最近一次同步错误码").HasMaxLength(64);
    }
}

internal sealed class SyncOutboxEntryConfiguration : IEntityTypeConfiguration<SyncOutboxEntry>
{
    public void Configure(EntityTypeBuilder<SyncOutboxEntry> builder)
    {
        builder.ToTable("sync_outbox", t => t.HasComment("待上传的本机行变更（触发器写入）"));
        builder.HasKey(e => e.Seq);
        builder.Property(e => e.Seq).Column("seq", ESqliteTypeName.INTEGER, "自增序号").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.TableName).Column("table_name", ESqliteTypeName.TEXT, "表名").HasMaxLength(64).IsRequired();
        builder.Property(e => e.RowId).Column("row_id", ESqliteTypeName.INTEGER, "行 rowid").IsRequired();
        builder.Property(e => e.SyncKey).Column("sync_key", ESqliteTypeName.TEXT, "删除时的同步键").HasMaxLength(512);
        builder.Property(e => e.Op).Column("op", ESqliteTypeName.TEXT, "upsert | delete").HasMaxLength(8).IsRequired();
        builder.Property(e => e.ChangedAt).Column("changed_at", ESqliteTypeName.INTEGER, "变更时间（Unix 毫秒）").IsRequired();
    }
}

internal sealed class SyncCursorConfiguration : IEntityTypeConfiguration<SyncCursor>
{
    public void Configure(EntityTypeBuilder<SyncCursor> builder)
    {
        builder.ToTable("sync_cursor", t => t.HasComment("已应用的远端设备进度"));
        builder.HasKey(e => e.DeviceId);
        builder.Property(e => e.DeviceId).Column("device_id", ESqliteTypeName.TEXT, "远端设备 ID").HasMaxLength(64).IsRequired();
        builder.Property(e => e.LastSeq).Column("last_seq", ESqliteTypeName.INTEGER, "已应用的最大段序号").IsRequired();
        builder.Property(e => e.DeviceName).Column("device_name", ESqliteTypeName.TEXT, "设备名").HasMaxLength(128);
        builder.Property(e => e.AppVersion).Column("app_version", ESqliteTypeName.TEXT, "应用版本").HasMaxLength(32);
        builder.Property(e => e.Platform).Column("platform", ESqliteTypeName.TEXT, "平台").HasMaxLength(32);
        builder.Property(e => e.RemoteUpdatedAt).Column("remote_updated_at", ESqliteTypeName.INTEGER, "远端设备最近上传（Unix 秒）");
        builder.Property(e => e.AppliedAt).Column("applied_at", ESqliteTypeName.INTEGER, "最近一次应用（Unix 秒）");
    }
}

internal sealed class SyncRowVersionConfiguration : IEntityTypeConfiguration<SyncRowVersion>
{
    public void Configure(EntityTypeBuilder<SyncRowVersion> builder)
    {
        builder.ToTable("sync_version", t => t.HasComment("同步行版本（最后写入胜出，删除保留为墓碑）"));
        builder.HasKey(e => new { e.TableName, e.SyncKey });
        builder.Property(e => e.TableName).Column("table_name", ESqliteTypeName.TEXT, "表名").HasMaxLength(64).IsRequired();
        builder.Property(e => e.SyncKey).Column("sync_key", ESqliteTypeName.TEXT, "同步键").HasMaxLength(512).IsRequired();
        builder.Property(e => e.ChangedAt).Column("changed_at", ESqliteTypeName.INTEGER, "版本时间（Unix 毫秒）").IsRequired();
        builder.Property(e => e.DeviceId).Column("device_id", ESqliteTypeName.TEXT, "写出该版本的设备").HasMaxLength(64).IsRequired();
        builder.Property(e => e.Deleted).Column("deleted", ESqliteTypeName.INTEGER, "是否为删除墓碑").IsRequired();
    }
}
