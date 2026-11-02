using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElectronNet.Migrations
{
    /// <inheritdoc />
    public partial class AddAchievementAndLibraryArchive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "achievement_game_archive",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    account_name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, comment: "登录用户账户名"),
                    steam_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, comment: "账号 Steam ID"),
                    app_id = table.Column<long>(type: "INTEGER", nullable: false, comment: "应用 ID"),
                    app_name = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false, comment: "应用名称"),
                    total = table.Column<int>(type: "INTEGER", nullable: false, comment: "成就总数"),
                    unlocked = table.Column<int>(type: "INTEGER", nullable: false, comment: "已解锁数"),
                    synced_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "最近归档时间（Unix 时间戳，秒）")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_achievement_game_archive", x => x.id);
                },
                comment: "游戏成就进度归档表");

            migrationBuilder.CreateTable(
                name: "achievement_target",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    account_name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, comment: "登录用户账户名"),
                    app_id = table.Column<long>(type: "INTEGER", nullable: false, comment: "应用 ID"),
                    app_name = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false, comment: "应用名称"),
                    internal_name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, comment: "成就内部名称"),
                    name = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false, comment: "成就名称"),
                    description = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false, comment: "成就描述"),
                    icon = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false, comment: "成就图标 URL"),
                    note = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true, comment: "备注"),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "加入清单时间（Unix 时间戳，秒）")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_achievement_target", x => x.id);
                },
                comment: "成就目标清单表");

            migrationBuilder.CreateTable(
                name: "achievement_unlock_archive",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    account_name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, comment: "登录用户账户名"),
                    steam_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, comment: "账号 Steam ID"),
                    app_id = table.Column<long>(type: "INTEGER", nullable: false, comment: "应用 ID"),
                    internal_name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, comment: "成就内部名称"),
                    name = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false, comment: "成就本地化名称"),
                    description = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false, comment: "成就本地化描述"),
                    icon = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false, comment: "成就图标 URL"),
                    hidden = table.Column<bool>(type: "INTEGER", nullable: false, comment: "是否隐藏成就"),
                    global_percent = table.Column<double>(type: "REAL", nullable: true, comment: "全球解锁率（%）"),
                    unlock_time = table.Column<long>(type: "INTEGER", nullable: false, comment: "解锁时间（Unix 时间戳，秒）"),
                    recorded_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "首次归档时间（Unix 时间戳，秒）")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_achievement_unlock_archive", x => x.id);
                },
                comment: "已解锁成就永久归档表");

            migrationBuilder.CreateTable(
                name: "library_acquisition",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    account_name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, comment: "登录用户账户名"),
                    app_id = table.Column<long>(type: "INTEGER", nullable: false, comment: "应用 ID（0 = 无应用的 package 占位）"),
                    package_id = table.Column<long>(type: "INTEGER", nullable: false, comment: "许可证 package ID（0 = 首次观测）"),
                    acquired_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "入库时间（Unix 时间戳，秒）")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_library_acquisition", x => x.id);
                },
                comment: "游戏入库时间表（许可证 / 首次观测）");

            migrationBuilder.CreateTable(
                name: "library_game_meta",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    account_name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, comment: "登录用户账户名"),
                    app_id = table.Column<long>(type: "INTEGER", nullable: false, comment: "应用 ID"),
                    status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true, comment: "游戏状态（playing/completed/abandoned/wantToPlay/shelved）"),
                    completed_at = table.Column<long>(type: "INTEGER", nullable: true, comment: "通关时间（Unix 时间戳，秒）"),
                    rating = table.Column<int>(type: "INTEGER", nullable: true, comment: "个人评分（1~10）"),
                    price = table.Column<double>(type: "REAL", nullable: true, comment: "购买价格"),
                    currency = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true, comment: "价格币种"),
                    note = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true, comment: "备注"),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "更新时间（Unix 时间戳，秒）")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_library_game_meta", x => x.id);
                },
                comment: "游戏档案信息表（状态 / 评分 / 价格）");

            migrationBuilder.CreateIndex(
                name: "achievement_game_archive_uidx",
                table: "achievement_game_archive",
                columns: new[] { "account_name", "app_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "achievement_target_uidx",
                table: "achievement_target",
                columns: new[] { "account_name", "app_id", "internal_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "achievement_unlock_archive_time_idx",
                table: "achievement_unlock_archive",
                columns: new[] { "account_name", "unlock_time" });

            migrationBuilder.CreateIndex(
                name: "achievement_unlock_archive_uidx",
                table: "achievement_unlock_archive",
                columns: new[] { "account_name", "app_id", "internal_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "library_acquisition_app_idx",
                table: "library_acquisition",
                columns: new[] { "account_name", "app_id" });

            migrationBuilder.CreateIndex(
                name: "library_acquisition_uidx",
                table: "library_acquisition",
                columns: new[] { "account_name", "package_id", "app_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "library_game_meta_uidx",
                table: "library_game_meta",
                columns: new[] { "account_name", "app_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "achievement_game_archive");

            migrationBuilder.DropTable(
                name: "achievement_target");

            migrationBuilder.DropTable(
                name: "achievement_unlock_archive");

            migrationBuilder.DropTable(
                name: "library_acquisition");

            migrationBuilder.DropTable(
                name: "library_game_meta");
        }
    }
}
