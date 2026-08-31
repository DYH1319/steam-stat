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

    /// <summary>
    /// 数据库模型创建
    /// </summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
}
