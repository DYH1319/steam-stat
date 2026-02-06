using System.Data;
using ElectronNet.Constants;
using ElectronNet.Enums;
using ElectronNet.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ElectronNet;

public class AppDbContext : DbContext
{
    private SqliteConnection? _sqliteConnection;

    public DbSet<GlobalStatus> GlobalStatusTable { get; set; }
    public DbSet<SteamUser> SteamUserTable { get; set; }
    public DbSet<SteamApp> SteamAppTable { get; set; }
    public DbSet<UseAppRecord> UseAppRecordTable { get; set; }

    // 单例模式（仅用于启动时的一次性操作）
    private static readonly Lock _syncRoot = new();

    /// <summary>
    /// 获取用于迁移的单例实例（仅在启动时使用）
    /// </summary>
    public static AppDbContext Instance
    {
        get
        {
            if (field == null)
            {
                lock (_syncRoot)
                {
                    field ??= new AppDbContext();
                }
            }

            return field;
        }
    }

    /// <summary>
    /// 创建新的 DbContext 实例（线程安全，推荐用于所有数据库操作）
    /// 使用完毕后应当 Dispose
    /// </summary>
    public static AppDbContext Create() => new();

    /// <summary>
    /// 数据库配置
    /// </summary>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        var connectionString = new SqliteConnectionStringBuilder()
        {
            Mode = SqliteOpenMode.ReadWriteCreate,
            DataSource = $"{Program.UserDataPath}/Database/steam-stat.db",
            Cache = SqliteCacheMode.Default,
            ForeignKeys = null,
            RecursiveTriggers = false,
            DefaultTimeout = 10,
            Pooling = true,
            Vfs = null
        }.ToString();
        _sqliteConnection = new SqliteConnection(connectionString);
        optionsBuilder.UseSqlite(_sqliteConnection);
    }

    /// <summary>
    /// 数据库模型创建
    /// </summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
        modelBuilder.ApplyConfiguration(new GlobalStatusConfiguration());
        modelBuilder.ApplyConfiguration(new SteamUserConfiguration());
        modelBuilder.ApplyConfiguration(new SteamAppConfiguration());
        modelBuilder.ApplyConfiguration(new UseAppRecordConfiguration());

        // 全局查询过滤器示例：只查询已安装的应用
        // modelBuilder.Entity<SteamApp>().HasQueryFilter(a => a.Installed);
    }

    /// <summary>
    /// 应用数据库迁移
    /// </summary>
    public async Task ApplyMigrationsAsync()
    {
        try
        {
            var pendingMigrations = (await Database.GetPendingMigrationsAsync()).ToList();

            if (pendingMigrations.Count > 0)
            {
                Console.WriteLine($"{ConsoleLogPrefix.DB} 检测到 {pendingMigrations.Count} 个待执行的迁移:");
                foreach (var migration in pendingMigrations)
                {
                    Console.WriteLine($"  - {migration}");
                }

                if (_sqliteConnection != null)
                {
                    Console.WriteLine($"{ConsoleLogPrefix.DB} 正在备份...");
                    if (_sqliteConnection.State != ConnectionState.Open) _sqliteConnection.Open();
                    _sqliteConnection.BackupDatabase(
                        new SqliteConnection(
                            new SqliteConnectionStringBuilder
                            {
                                Mode = SqliteOpenMode.ReadWriteCreate,
                                DataSource = $"{Program.UserDataPath}/Database/steam-stat.bak"
                            }.ToString()
                        )
                    );
                    Console.WriteLine($"{ConsoleLogPrefix.DB} 备份完成");
                }

                Console.WriteLine($"{ConsoleLogPrefix.DB} 正在应用迁移...");
                await Database.MigrateAsync();
                Console.WriteLine($"{ConsoleLogPrefix.DB} 迁移完成");
            }
            else
            {
                Console.WriteLine($"{ConsoleLogPrefix.DB} 数据库已是最新版本，无需迁移");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ConsoleLogPrefix.ERROR} 数据库备份 / 迁移失败: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// GlobalStatus 表配置
    /// </summary>
    private class GlobalStatusConfiguration : IEntityTypeConfiguration<GlobalStatus>
    {
        public void Configure(EntityTypeBuilder<GlobalStatus> builder)
        {
            builder.ToTable("global_status", t => t
                .HasComment("全局状态表（单行数据表）")
                .HasCheckConstraint("global_status_check_id", "id = 1")
            );

            builder.HasKey(e => e.Id);

            builder.Property(e => e.Id)
                .HasColumnName("id")
                .HasColumnType(nameof(SqliteTypeName.INTEGER))
                .HasComment("ID")
                .IsRequired()
                .HasDefaultValue(1)
                .ValueGeneratedNever();

            builder.Property(e => e.SteamPath)
                .HasColumnName("steam_path")
                .HasColumnType(nameof(SqliteTypeName.TEXT))
                .HasComment("Steam 安装路径")
                .HasMaxLength(1024);

            builder.Property(e => e.SteamExePath)
                .HasColumnName("steam_exe_path")
                .HasColumnType(nameof(SqliteTypeName.TEXT))
                .HasComment("Steam 可执行文件路径")
                .HasMaxLength(1024);

            builder.Property(e => e.SteamPid)
                .HasColumnName("steam_pid")
                .HasColumnType(nameof(SqliteTypeName.INTEGER))
                .HasComment("Steam 进程 PID");

            builder.Property(e => e.SteamClientDllPath)
                .HasColumnName("steam_client_dll_path")
                .HasColumnType(nameof(SqliteTypeName.TEXT))
                .HasComment("steamclient.dll 文件路径")
                .HasMaxLength(1024);

            builder.Property(e => e.SteamClientDll64Path)
                .HasColumnName("steam_client_dll_64_path")
                .HasColumnType(nameof(SqliteTypeName.TEXT))
                .HasComment("steamclient64.dll 文件路径")
                .HasMaxLength(1024);

            builder.Property(e => e.ActiveUserSteamId)
                .HasColumnName("active_user_steam_id")
                .HasColumnType(nameof(SqliteTypeName.INTEGER))
                .HasComment("当前登录用户的 Steam ID");

            builder.Property(e => e.RunningAppId)
                .HasColumnName("running_app_id")
                .HasColumnType(nameof(SqliteTypeName.INTEGER))
                .HasComment("Steam 对外显示的当前运行的 App ID（同时只有一个）");

            builder.Property(e => e.RefreshTime)
                .HasColumnName("refresh_time")
                .HasColumnType(nameof(SqliteTypeName.INTEGER))
                .HasComment("刷新时间")
                .IsRequired();

            builder.Property(e => e.SteamUserRefreshTime)
                .HasColumnName("steam_user_refresh_time")
                .HasColumnType(nameof(SqliteTypeName.INTEGER))
                .HasComment("steamUser 表刷新时间");
            
            builder.Property(e => e.SteamAppRefreshTime)
                .HasColumnName("steam_app_refresh_time")
                .HasColumnType(nameof(SqliteTypeName.INTEGER))
                .HasComment("steamApp 表刷新时间");
        }
    }

    /// <summary>
    /// SteamUser 表配置
    /// </summary>
    private class SteamUserConfiguration : IEntityTypeConfiguration<SteamUser>
    {
        public void Configure(EntityTypeBuilder<SteamUser> builder)
        {
            builder.ToTable("steam_user", t => t
                .HasComment("Steam 用户表")
            );

            builder.HasKey(e => e.Id);

            builder.HasIndex(e => e.SteamId).IsUnique();
            builder.HasIndex(e => e.AccountId).IsUnique();

            builder.Property(e => e.Id)
                .HasColumnName("id")
                .HasColumnType(nameof(SqliteTypeName.INTEGER))
                .HasComment("ID")
                .IsRequired()
                .ValueGeneratedOnAdd();

            builder.Property(e => e.SteamId)
                .HasColumnName("steam_id")
                .HasColumnType(nameof(SqliteTypeName.INTEGER))
                .HasComment("Steam ID")
                .IsRequired();

            builder.Property(e => e.AccountId)
                .HasColumnName("account_id")
                .HasColumnType(nameof(SqliteTypeName.INTEGER))
                .HasComment("Account ID")
                .IsRequired();

            builder.Property(e => e.AccountName)
                .HasColumnName("account_name")
                .HasColumnType(nameof(SqliteTypeName.TEXT))
                .HasComment("账号名")
                .HasMaxLength(256)
                .IsRequired();

            builder.Property(e => e.PersonaName)
                .HasColumnName("persona_name")
                .HasColumnType(nameof(SqliteTypeName.TEXT))
                .HasComment("昵称")
                .HasMaxLength(256);

            builder.Property(e => e.RememberPassword)
                .HasColumnName("remember_password")
                .HasColumnType(nameof(SqliteTypeName.INTEGER))
                .HasComment("是否记住密码");

            builder.Property(e => e.WantsOfflineMode)
                .HasColumnName("wants_offline_mode")
                .HasColumnType(nameof(SqliteTypeName.INTEGER))
                .HasComment("是否开启离线模式");

            builder.Property(e => e.SkipOfflineModeWarning)
                .HasColumnName("skip_offline_mode_warning")
                .HasColumnType(nameof(SqliteTypeName.INTEGER))
                .HasComment("是否跳过离线模式警告");

            builder.Property(e => e.AllowAutoLogin)
                .HasColumnName("allow_auto_login")
                .HasColumnType(nameof(SqliteTypeName.INTEGER))
                .HasComment("是否允许自动登录");

            builder.Property(e => e.MostRecent)
                .HasColumnName("most_recent")
                .HasColumnType(nameof(SqliteTypeName.INTEGER))
                .HasComment("是否最近登录");

            builder.Property(e => e.Timestamp)
                .HasColumnName("timestamp")
                .HasColumnType(nameof(SqliteTypeName.INTEGER))
                .HasComment("最近登录时间");

            builder.Property(e => e.AvatarFull)
                .HasColumnName("avatar_full")
                .HasColumnType(nameof(SqliteTypeName.TEXT))
                .HasComment("全尺寸头像（184x184）Base64")
                .HasMaxLength(int.MaxValue);

            builder.Property(e => e.AvatarMedium)
                .HasColumnName("avatar_medium")
                .HasColumnType(nameof(SqliteTypeName.TEXT))
                .HasComment("中等尺寸头像（64x64）Base64")
                .HasMaxLength(int.MaxValue);

            builder.Property(e => e.AvatarSmall)
                .HasColumnName("avatar_small")
                .HasColumnType(nameof(SqliteTypeName.TEXT))
                .HasComment("小尺寸头像（32x32）Base64")
                .HasMaxLength(int.MaxValue);

            builder.Property(e => e.AnimatedAvatar)
                .HasColumnName("animated_avatar")
                .HasColumnType(nameof(SqliteTypeName.TEXT))
                .HasComment("动画头像 Base64")
                .HasMaxLength(int.MaxValue);

            builder.Property(e => e.AvatarFrame)
                .HasColumnName("avatar_frame")
                .HasColumnType(nameof(SqliteTypeName.TEXT))
                .HasComment("头像边框 Base64")
                .HasMaxLength(int.MaxValue);

            builder.Property(e => e.Level)
                .HasColumnName("level")
                .HasColumnType(nameof(SqliteTypeName.INTEGER))
                .HasComment("等级");

            builder.Property(e => e.LevelClass)
                .HasColumnName("level_class")
                .HasColumnType(nameof(SqliteTypeName.TEXT))
                .HasComment("等级样式类")
                .HasMaxLength(256);
        }
    }

    /// <summary>
    /// SteamApp 表配置
    /// </summary>
    private class SteamAppConfiguration : IEntityTypeConfiguration<SteamApp>
    {
        public void Configure(EntityTypeBuilder<SteamApp> builder)
        {
            builder.ToTable("steam_app", t => t
                .HasComment("Steam 应用表")
            );

            builder.HasKey(e => e.Id);

            builder.HasIndex(e => e.AppId).IsUnique();
            builder.HasIndex(e => e.Name, "steam_app_name_idx");
            builder.HasIndex(e => e.Installed, "steam_app_installed_idx");

            builder.Property(e => e.Id)
                .HasColumnName("id")
                .HasColumnType(nameof(SqliteTypeName.INTEGER))
                .HasComment("ID")
                .IsRequired()
                .ValueGeneratedOnAdd();

            builder.Property(e => e.AppId)
                .HasColumnName("app_id")
                .HasColumnType(nameof(SqliteTypeName.INTEGER))
                .HasComment("App ID")
                .IsRequired();

            builder.Property(e => e.Name)
                .HasColumnName("name")
                .HasColumnType(nameof(SqliteTypeName.TEXT))
                .HasComment("应用名称")
                .HasMaxLength(1024);

            builder.Property(e => e.NameLocalizedJson)
                .HasColumnName("name_localized")
                .HasColumnType(nameof(SqliteTypeName.TEXT))
                .HasComment("应用本地化名称 JSON 对象")
                .HasMaxLength(int.MaxValue)
                .IsRequired()
                .HasDefaultValue("{}");

            builder.Property(e => e.Installed)
                .HasColumnName("installed")
                .HasColumnType(nameof(SqliteTypeName.INTEGER))
                .HasComment("是否已本地安装")
                .IsRequired();

            builder.Property(e => e.InstallDir)
                .HasColumnName("install_dir")
                .HasColumnType(nameof(SqliteTypeName.TEXT))
                .HasComment("本地安装目录名称")
                .HasMaxLength(int.MaxValue);

            builder.Property(e => e.InstallDirPath)
                .HasColumnName("install_dir_path")
                .HasColumnType(nameof(SqliteTypeName.TEXT))
                .HasComment("本地安装目录绝对路径")
                .HasMaxLength(int.MaxValue);

            builder.Property(e => e.AppOnDisk)
                .HasColumnName("app_on_disk")
                .HasColumnType(nameof(SqliteTypeName.INTEGER))
                .HasComment("应用文件占用大小");

            builder.Property(e => e.AppOnDiskReal)
                .HasColumnName("app_on_disk_real")
                .HasColumnType(nameof(SqliteTypeName.INTEGER))
                .HasComment("应用文件真实占用大小");

            builder.Property(e => e.IsRunning)
                .HasColumnName("is_running")
                .HasColumnType(nameof(SqliteTypeName.INTEGER))
                .HasComment("是否正在运行")
                .IsRequired()
                .HasDefaultValue(false);

            builder.Property(e => e.Type)
                .HasColumnName("type")
                .HasColumnType(nameof(SqliteTypeName.TEXT))
                .HasComment("应用类型")
                .HasMaxLength(128);

            builder.Property(e => e.Developer)
                .HasColumnName("developer")
                .HasColumnType(nameof(SqliteTypeName.TEXT))
                .HasComment("开发者")
                .HasMaxLength(256);

            builder.Property(e => e.Publisher)
                .HasColumnName("publisher")
                .HasColumnType(nameof(SqliteTypeName.TEXT))
                .HasComment("发布者")
                .HasMaxLength(256);

            builder.Property(e => e.SteamReleaseDate)
                .HasColumnName("steam_release_date")
                .HasColumnType(nameof(SqliteTypeName.INTEGER))
                .HasComment("发布日期");

            builder.Property(e => e.IsFreeApp)
                .HasColumnName("is_free_app")
                .HasColumnType(nameof(SqliteTypeName.INTEGER))
                .HasComment("是否是免费应用");
        }
    }

    /// <summary>
    /// UseAppRecord 表配置
    /// </summary>
    private class UseAppRecordConfiguration : IEntityTypeConfiguration<UseAppRecord>
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
                .HasColumnType(nameof(SqliteTypeName.INTEGER))
                .HasComment("ID")
                .IsRequired()
                .ValueGeneratedOnAdd();

            builder.Property(e => e.AppId)
                .HasColumnName("app_id")
                .HasColumnType(nameof(SqliteTypeName.INTEGER))
                .HasComment("使用的 App ID")
                .IsRequired();

            builder.Property(e => e.SteamId)
                .HasColumnName("steam_id")
                .HasColumnType(nameof(SqliteTypeName.INTEGER))
                .HasComment("使用 App 的 Steam ID")
                .IsRequired();

            builder.Property(e => e.StartTime)
                .HasColumnName("start_time")
                .HasColumnType(nameof(SqliteTypeName.INTEGER))
                .HasComment("开始使用的 Unix 时间戳")
                .IsRequired();

            builder.Property(e => e.EndTime)
                .HasColumnName("end_time")
                .HasColumnType(nameof(SqliteTypeName.INTEGER))
                .HasComment("结束使用的 Unix 时间戳");

            builder.Property(e => e.Duration)
                .HasColumnName("duration")
                .HasColumnType(nameof(SqliteTypeName.INTEGER))
                .HasComment("持续使用时间");
        }
    }
}