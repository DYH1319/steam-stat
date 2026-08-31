using ElectronNet.Enums;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SteamStat.Core.Features.LocalStatus.Persistence;

/// <summary>
/// GlobalStatus 表配置
/// </summary>
internal sealed class GlobalStatusConfiguration : IEntityTypeConfiguration<GlobalStatus>
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
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("ID")
            .IsRequired()
            .HasDefaultValue(1)
            .ValueGeneratedNever();

        builder.Property(e => e.SteamPath)
            .HasColumnName("steam_path")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("Steam 安装路径")
            .HasMaxLength(1024);

        builder.Property(e => e.SteamExePath)
            .HasColumnName("steam_exe_path")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("Steam 可执行文件路径")
            .HasMaxLength(1024);

        builder.Property(e => e.SteamPid)
            .HasColumnName("steam_pid")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("Steam 进程 PID");

        builder.Property(e => e.SteamClientDllPath)
            .HasColumnName("steam_client_dll_path")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("steamclient.dll 文件路径")
            .HasMaxLength(1024);

        builder.Property(e => e.SteamClientDll64Path)
            .HasColumnName("steam_client_dll_64_path")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("steamclient64.dll 文件路径")
            .HasMaxLength(1024);

        builder.Property(e => e.ActiveUserSteamId)
            .HasColumnName("active_user_steam_id")
            .HasColumnType(nameof(ESqliteTypeName.TEXT))
            .HasComment("当前登录用户的 Steam ID")
            .HasMaxLength(64);

        builder.Property(e => e.RunningAppId)
            .HasColumnName("running_app_id")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("Steam 对外显示的当前运行的 App ID（同时只有一个）");

        builder.Property(e => e.RefreshTime)
            .HasColumnName("refresh_time")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("刷新时间")
            .IsRequired();

        builder.Property(e => e.SteamUserRefreshTime)
            .HasColumnName("steam_user_refresh_time")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("steamUser 表刷新时间");

        builder.Property(e => e.SteamAppRefreshTime)
            .HasColumnName("steam_app_refresh_time")
            .HasColumnType(nameof(ESqliteTypeName.INTEGER))
            .HasComment("steamApp 表刷新时间");
    }
}
