using ElectronNet.Features.SteamCache.Persistence;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;

namespace ElectronNet;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<GlobalStatus> GlobalStatusTable => Set<GlobalStatus>();
    public DbSet<SteamUser> SteamUserTable => Set<SteamUser>();
    public DbSet<SteamApp> SteamAppTable => Set<SteamApp>();
    public DbSet<UseAppRecord> UseAppRecordTable => Set<UseAppRecord>();
    public DbSet<SteamLoginToken> SteamLoginTokenTable => Set<SteamLoginToken>();
    public DbSet<FriendStatusRecord> FriendStatusRecordTable => Set<FriendStatusRecord>();
    public DbSet<FriendTracking> FriendTrackingTable => Set<FriendTracking>();
    public DbSet<RichPresenceRecord> RichPresenceRecordTable => Set<RichPresenceRecord>();
    public DbSet<SteamResourceCacheEntry> SteamResourceCacheTable => Set<SteamResourceCacheEntry>();
    public DbSet<Device> DeviceTable => Set<Device>();
    public DbSet<PlaytimeSnapshot> PlaytimeSnapshotTable => Set<PlaytimeSnapshot>();
    public DbSet<SessionImportBatch> SessionImportBatchTable => Set<SessionImportBatch>();
    public DbSet<AchievementUnlockArchive> AchievementUnlockArchiveTable => Set<AchievementUnlockArchive>();
    public DbSet<AchievementGameArchive> AchievementGameArchiveTable => Set<AchievementGameArchive>();
    public DbSet<AchievementSetArchive> AchievementSetArchiveTable => Set<AchievementSetArchive>();
    public DbSet<AchievementSchemaState> AchievementSchemaStateTable => Set<AchievementSchemaState>();
    public DbSet<AchievementSchemaChange> AchievementSchemaChangeTable => Set<AchievementSchemaChange>();
    public DbSet<AchievementTarget> AchievementTargetTable => Set<AchievementTarget>();
    public DbSet<LibraryAcquisition> LibraryAcquisitionTable => Set<LibraryAcquisition>();
    public DbSet<LibraryGameMeta> LibraryGameMetaTable => Set<LibraryGameMeta>();
    public DbSet<ScreenshotArchive> ScreenshotArchiveTable => Set<ScreenshotArchive>();
    public DbSet<SaveBackupTarget> SaveBackupTargetTable => Set<SaveBackupTarget>();
    public DbSet<SaveBackupSnapshot> SaveBackupSnapshotTable => Set<SaveBackupSnapshot>();
    public DbSet<SaveBackupFile> SaveBackupFileTable => Set<SaveBackupFile>();
    public DbSet<ProfileHistoryEntry> ProfileHistoryTable => Set<ProfileHistoryEntry>();
    public DbSet<SyncControl> SyncControlTable => Set<SyncControl>();
    public DbSet<SyncOutboxEntry> SyncOutboxTable => Set<SyncOutboxEntry>();
    public DbSet<SyncCursor> SyncCursorTable => Set<SyncCursor>();
    public DbSet<SyncRowVersion> SyncVersionTable => Set<SyncRowVersion>();

    /// <summary>
    /// 数据库模型创建
    /// </summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
}
