using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElectronNet.Migrations
{
    /// <inheritdoc />
    public partial class EnsureChangeSteamIdFromIntegerToText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. 创建临时表及其索引
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
                    active_user_steam_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true, comment: "当前登录用户的 Steam ID"),
                    running_app_id = table.Column<int>(type: "INTEGER", nullable: true, comment: "Steam 对外显示的当前运行的 App ID（同时只有一个）"),
                    refresh_time = table.Column<int>(type: "INTEGER", nullable: false, comment: "刷新时间"),
                    steam_user_refresh_time = table.Column<int>(type: "INTEGER", nullable: true, comment: "steamUser 表刷新时间"),
                    steam_app_refresh_time = table.Column<int>(type: "INTEGER", nullable: true, comment: "steamApp 表刷新时间")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_global_status", x => x.id);
                    table.CheckConstraint("global_status_check_id", "id = 1");
                },
                comment: "全局状态表（单行数据表）");
            
            migrationBuilder.CreateTable(
                name: "steam_user_temp",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    steam_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, comment: "Steam ID"),
                    account_id = table.Column<int>(type: "INTEGER", nullable: false, comment: "Account ID"),
                    account_name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, comment: "账号名"),
                    persona_name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true, comment: "昵称"),
                    remember_password = table.Column<bool>(type: "INTEGER", nullable: true, comment: "是否记住密码"),
                    wants_offline_mode = table.Column<bool>(type: "INTEGER", nullable: true, comment: "是否开启离线模式"),
                    skip_offline_mode_warning = table.Column<bool>(type: "INTEGER", nullable: true, comment: "是否跳过离线模式警告"),
                    allow_auto_login = table.Column<bool>(type: "INTEGER", nullable: true, comment: "是否允许自动登录"),
                    most_recent = table.Column<bool>(type: "INTEGER", nullable: true, comment: "是否最近登录"),
                    timestamp = table.Column<int>(type: "INTEGER", nullable: true, comment: "最近登录时间"),
                    avatar_full = table.Column<string>(type: "TEXT", maxLength: 2147483647, nullable: true, comment: "全尺寸头像（184x184）Base64"),
                    avatar_medium = table.Column<string>(type: "TEXT", maxLength: 2147483647, nullable: true, comment: "中等尺寸头像（64x64）Base64"),
                    avatar_small = table.Column<string>(type: "TEXT", maxLength: 2147483647, nullable: true, comment: "小尺寸头像（32x32）Base64"),
                    animated_avatar = table.Column<string>(type: "TEXT", maxLength: 2147483647, nullable: true, comment: "动画头像 Base64"),
                    avatar_frame = table.Column<string>(type: "TEXT", maxLength: 2147483647, nullable: true, comment: "头像边框 Base64"),
                    level = table.Column<int>(type: "INTEGER", nullable: true, comment: "等级"),
                    level_class = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true, comment: "等级样式类")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_steam_user", x => x.id);
                },
                comment: "Steam 用户表");

            migrationBuilder.CreateTable(
                name: "use_app_record_temp",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    app_id = table.Column<int>(type: "INTEGER", nullable: false, comment: "使用的 App ID"),
                    steam_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, comment: "使用 App 的 Steam ID"),
                    start_time = table.Column<int>(type: "INTEGER", nullable: false, comment: "开始使用的 Unix 时间戳"),
                    end_time = table.Column<int>(type: "INTEGER", nullable: true, comment: "结束使用的 Unix 时间戳"),
                    duration = table.Column<int>(type: "INTEGER", nullable: true, comment: "持续使用时间")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_use_app_record", x => x.id);
                },
                comment: "应用使用记录表");

            // 2. 复制数据
            migrationBuilder.Sql(@"
                INSERT INTO global_status_temp 
                SELECT 
                    id,
                    steam_path,
                    steam_exe_path,
                    steam_pid,
                    steam_client_dll_path,
                    steam_client_dll_64_path,
                    CAST(active_user_steam_id AS TEXT),
                    running_app_id,
                    refresh_time,
                    steam_user_refresh_time,
                    steam_app_refresh_time
                FROM global_status;
            ");

            migrationBuilder.Sql(@"
                INSERT INTO steam_user_temp 
                SELECT 
                    id,
                    CAST(steam_id AS TEXT),
                    account_id,
                    account_name,
                    persona_name,
                    remember_password,
                    wants_offline_mode,
                    skip_offline_mode_warning,
                    allow_auto_login,
                    most_recent,
                    `timestamp`,
                    avatar_full,
                    avatar_medium,
                    avatar_small,
                    animated_avatar,
                    avatar_frame,
                    level,
                    level_class
                FROM steam_user;
            ");
            
            migrationBuilder.Sql(@"
                INSERT INTO use_app_record_temp 
                SELECT 
                    id,
                    app_id,
                    CAST(steam_id AS TEXT),
                    start_time,
                    end_time,
                    duration
                FROM use_app_record;
            ");
            
            // 3. 删除原表
            migrationBuilder.DropTable("global_status");
            migrationBuilder.DropTable("steam_user");
            migrationBuilder.DropTable("use_app_record");
            
            // 4. 重命名临时表
            migrationBuilder.RenameTable("global_status_temp", null, "global_status", null);
            migrationBuilder.RenameTable("steam_user_temp", null, "steam_user", null);
            migrationBuilder.RenameTable("use_app_record_temp", null, "use_app_record", null);
            
            // 5. 设置表索引
            migrationBuilder.CreateIndex(
                name: "IX_steam_user_account_id",
                table: "steam_user",
                column: "account_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_steam_user_steam_id",
                table: "steam_user",
                column: "steam_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "use_app_record_app_id_idx",
                table: "use_app_record",
                column: "app_id");

            migrationBuilder.CreateIndex(
                name: "use_app_record_start_time_idx",
                table: "use_app_record",
                column: "start_time");

            migrationBuilder.CreateIndex(
                name: "use_app_record_steam_id_app_id_idx",
                table: "use_app_record",
                columns: new[] { "steam_id", "app_id" });

            migrationBuilder.CreateIndex(
                name: "use_app_record_steam_id_idx",
                table: "use_app_record",
                column: "steam_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ignore
        }
    }
}
