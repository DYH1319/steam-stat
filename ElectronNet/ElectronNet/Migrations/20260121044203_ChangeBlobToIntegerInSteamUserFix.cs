using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElectronNet.Migrations
{
    /// <inheritdoc />
    public partial class ChangeBlobToIntegerInSteamUserFix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. 创建临时表
            migrationBuilder.CreateTable(
                name: "steam_user_temp",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    steam_id = table.Column<long>(type: "INTEGER", nullable: false, comment: "Steam ID"),
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
            
            // 2. 复制数据，将 blob 转换为 integer
            migrationBuilder.Sql(@"
                INSERT INTO steam_user_temp 
                SELECT 
                    id,
                    CASE 
                        WHEN typeof(steam_id) = 'integer' THEN steam_id
                        WHEN typeof(steam_id) = 'blob' THEN CAST(steam_id AS INTEGER)
                        ELSE 0 
                    END,
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
            
            // 3. 删除原表
            migrationBuilder.DropTable("steam_user");
            
            // 4. 重命名临时表
            migrationBuilder.RenameTable("steam_user_temp", null, "steam_user", null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
