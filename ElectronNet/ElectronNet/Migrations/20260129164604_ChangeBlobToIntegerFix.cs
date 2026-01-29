using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElectronNet.Migrations
{
    /// <inheritdoc />
    public partial class ChangeBlobToIntegerFix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. 创建临时表
            migrationBuilder.CreateTable(
                name: "global_status_temp",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1, comment: "ID"),
                    steam_path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true, comment: "Steam 安装路径"),
                    steam_exe_path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true, comment: "Steam 可执行文件路径"),
                    steam_pid = table.Column<int>(type: "INTEGER", nullable: true, comment: "Steam 进程 PID"),
                    steam_client_dll_path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true, comment: "steamclient.dll 文件路径"),
                    steam_client_dll_64_path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true, comment: "steamclient64.dll 文件路径"),
                    active_user_steam_id = table.Column<long>(type: "INTEGER", nullable: true, comment: "当前登录用户的 Steam ID"),
                    running_app_id = table.Column<int>(type: "INTEGER", nullable: true, comment: "Steam 对外显示的当前运行的 App ID（同时只有一个）"),
                    refresh_time = table.Column<int>(type: "INTEGER", nullable: false, comment: "刷新时间"),
                    steam_user_refresh_time = table.Column<int>(type: "INTEGER", nullable: true, comment: "steamUser 表刷新时间")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_global_status", x => x.id);
                    table.CheckConstraint("global_status_check_id", "id = 1");
                },
                comment: "全局状态表（单行数据表）");

            migrationBuilder.CreateTable(
                name: "steam_app_temp",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    app_id = table.Column<int>(type: "INTEGER", nullable: false, comment: "App ID"),
                    name = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true, comment: "应用名称"),
                    name_localized = table.Column<string>(type: "TEXT", maxLength: 2147483647, nullable: false, defaultValue: "{}", comment: "应用本地化名称 JSON 对象"),
                    installed = table.Column<bool>(type: "INTEGER", nullable: false, comment: "是否已本地安装"),
                    install_dir = table.Column<string>(type: "TEXT", maxLength: 2147483647, nullable: true, comment: "本地安装目录名称"),
                    install_dir_path = table.Column<string>(type: "TEXT", maxLength: 2147483647, nullable: true, comment: "本地安装目录绝对路径"),
                    app_on_disk = table.Column<long>(type: "INTEGER", nullable: true, comment: "应用文件占用大小"),
                    app_on_disk_real = table.Column<long>(type: "INTEGER", nullable: true, comment: "应用文件真实占用大小"),
                    is_running = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false, comment: "是否正在运行"),
                    type = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true, comment: "应用类型"),
                    developer = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true, comment: "开发者"),
                    publisher = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true, comment: "发布者"),
                    steam_release_date = table.Column<int>(type: "INTEGER", nullable: true, comment: "发布日期"),
                    is_free_app = table.Column<bool>(type: "INTEGER", nullable: true, comment: "是否是免费应用"),
                    refresh_time = table.Column<int>(type: "INTEGER", nullable: false, comment: "刷新时间")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_steam_app", x => x.id);
                },
                comment: "Steam 应用表");

            migrationBuilder.CreateTable(
                name: "use_app_record_temp",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    app_id = table.Column<int>(type: "INTEGER", nullable: false, comment: "使用的 App ID"),
                    steam_id = table.Column<long>(type: "INTEGER", nullable: false, comment: "使用 App 的 Steam ID"),
                    start_time = table.Column<int>(type: "INTEGER", nullable: false, comment: "开始使用的 Unix 时间戳"),
                    end_time = table.Column<int>(type: "INTEGER", nullable: true, comment: "结束使用的 Unix 时间戳"),
                    duration = table.Column<int>(type: "INTEGER", nullable: true, comment: "持续使用时间")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_use_app_record", x => x.id);
                },
                comment: "应用使用记录表");
            
            // 2. 复制数据，将 blob 转换为 integer
            migrationBuilder.Sql(@"
                INSERT INTO global_status_temp 
                SELECT 
                    id,
                    steam_path,
                    steam_exe_path,
                    steam_pid,
                    steam_client_dll_path,
                    steam_client_dll_64_path,
                    CASE 
                        WHEN typeof(active_user_steam_id) = 'integer' THEN active_user_steam_id
                        WHEN typeof(active_user_steam_id) = 'blob' THEN CAST(active_user_steam_id AS INTEGER)
                        ELSE 0 
                    END,
                    running_app_id,
                    refresh_time,
                    steam_user_refresh_time
                FROM global_status;
            ");
            
            migrationBuilder.Sql(@"
                INSERT INTO steam_app_temp 
                SELECT 
                    id,
                    app_id,
                    name,
                    name_localized,
                    installed,
                    install_dir,
                    install_dir_path,
                    CASE 
                        WHEN typeof(app_on_disk) = 'integer' THEN app_on_disk
                        WHEN typeof(app_on_disk) = 'blob' THEN CAST(app_on_disk AS INTEGER)
                        ELSE 0 
                    END,
                    CASE 
                        WHEN typeof(app_on_disk_real) = 'integer' THEN app_on_disk_real
                        WHEN typeof(app_on_disk_real) = 'blob' THEN CAST(app_on_disk_real AS INTEGER)
                        ELSE 0 
                    END,
                    is_running,
                    type,
                    developer,
                    publisher,
                    steam_release_date,
                    is_free_app,
                    refresh_time
                FROM steam_app;
            ");
            
            migrationBuilder.Sql(@"
                INSERT INTO use_app_record_temp 
                SELECT 
                    id,
                    app_id,
                    CASE 
                        WHEN typeof(steam_id) = 'integer' THEN steam_id
                        WHEN typeof(steam_id) = 'blob' THEN CAST(steam_id AS INTEGER)
                        ELSE 0 
                    END,
                    start_time,
                    end_time,
                    duration
                FROM use_app_record;
            ");
            
            // 3. 删除原表
            migrationBuilder.DropTable("global_status");
            migrationBuilder.DropTable("steam_app");
            migrationBuilder.DropTable("use_app_record");
            
            // 4. 重命名临时表
            migrationBuilder.RenameTable("global_status_temp", null, "global_status", null);
            migrationBuilder.RenameTable("steam_app_temp", null, "steam_app", null);
            migrationBuilder.RenameTable("use_app_record_temp", null, "use_app_record", null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ignore
        }
    }
}
