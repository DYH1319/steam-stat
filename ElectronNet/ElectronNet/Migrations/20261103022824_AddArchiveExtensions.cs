using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElectronNet.Migrations
{
    /// <inheritdoc />
    public partial class AddArchiveExtensions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "profile_history",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    steam_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "64 位 SteamID"),
                    field = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "字段（personaName/avatar/level/favoriteBadge）"),
                    value = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false, comment: "新值"),
                    previous_value = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true, comment: "旧值（首次观测为空）"),
                    observed_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "观测时间（Unix 时间戳，秒）"),
                    source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, comment: "来源（cm/friends/local）"),
                    local_path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true, comment: "头像本地存档路径")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profile_history", x => x.id);
                },
                comment: "自己的资料变化记录表（昵称/头像/等级/展示徽章）");

            migrationBuilder.CreateTable(
                name: "save_backup_file",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    snapshot_id = table.Column<int>(type: "INTEGER", nullable: false, comment: "版本 ID"),
                    relative_path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false, comment: "相对路径（/ 分隔）"),
                    size = table.Column<long>(type: "INTEGER", nullable: false, comment: "文件大小（字节）"),
                    modified_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "文件修改时间（Unix 时间戳，秒）"),
                    sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, comment: "内容 SHA-256（小写十六进制）")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_save_backup_file", x => x.id);
                },
                comment: "存档版本文件清单表");

            migrationBuilder.CreateTable(
                name: "save_backup_snapshot",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    target_id = table.Column<int>(type: "INTEGER", nullable: false, comment: "备份目录 ID"),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "快照时间（Unix 时间戳，秒）"),
                    file_count = table.Column<int>(type: "INTEGER", nullable: false, comment: "文件数"),
                    total_size = table.Column<long>(type: "INTEGER", nullable: false, comment: "总大小（字节）"),
                    added_size = table.Column<long>(type: "INTEGER", nullable: false, comment: "新写入备份库的字节数"),
                    changed_files = table.Column<int>(type: "INTEGER", nullable: false, comment: "相比上一版本变化的文件数"),
                    trigger = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, comment: "触发方式（manual/schedule/sessionEnd）"),
                    manifest_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, comment: "清单指纹"),
                    latest_modified_at = table.Column<long>(type: "INTEGER", nullable: true, comment: "最近修改的文件时间（Unix 时间戳，秒）")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_save_backup_snapshot", x => x.id);
                },
                comment: "存档版本表");

            migrationBuilder.CreateTable(
                name: "save_backup_target",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    app_id = table.Column<long>(type: "INTEGER", nullable: false, comment: "应用 ID（0 = 未关联）"),
                    kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, comment: "目录来源（steamCloud/autoCloud/custom）"),
                    path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false, comment: "目录绝对路径（规范化）"),
                    steam_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true, comment: "所属账号 64 位 SteamID"),
                    label = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true, comment: "备注名"),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false, comment: "是否参与自动快照"),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "添加时间（Unix 时间戳，秒）"),
                    last_scan_at = table.Column<long>(type: "INTEGER", nullable: true, comment: "最近一次扫描时间（Unix 时间戳，秒）"),
                    last_snapshot_at = table.Column<long>(type: "INTEGER", nullable: true, comment: "最近一次产生版本的时间（Unix 时间戳，秒）"),
                    last_error = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true, comment: "最近一次失败原因")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_save_backup_target", x => x.id);
                },
                comment: "存档备份目录表");

            migrationBuilder.CreateTable(
                name: "screenshot_archive",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    account_id = table.Column<long>(type: "INTEGER", nullable: false, comment: "Steam AccountID"),
                    steam_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "64 位 SteamID"),
                    app_id = table.Column<long>(type: "INTEGER", nullable: false, comment: "应用 ID"),
                    file_name = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false, comment: "截图文件名"),
                    file_path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false, comment: "截图绝对路径"),
                    thumbnail_path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true, comment: "缩略图绝对路径"),
                    taken_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "拍摄时间（Unix 时间戳，秒）"),
                    file_size = table.Column<long>(type: "INTEGER", nullable: false, comment: "文件大小（字节）"),
                    width = table.Column<int>(type: "INTEGER", nullable: true, comment: "宽度（像素）"),
                    height = table.Column<int>(type: "INTEGER", nullable: true, comment: "高度（像素）"),
                    caption = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true, comment: "截图说明"),
                    indexed_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "最近一次索引时间（Unix 时间戳，秒）"),
                    missing_since = table.Column<long>(type: "INTEGER", nullable: true, comment: "文件不再存在的首次发现时间（Unix 时间戳，秒）")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_screenshot_archive", x => x.id);
                },
                comment: "截图档案表（Steam 截图目录索引）");

            migrationBuilder.CreateIndex(
                name: "profile_history_idx",
                table: "profile_history",
                columns: new[] { "steam_id", "field", "observed_at" });

            migrationBuilder.CreateIndex(
                name: "save_backup_file_sha_idx",
                table: "save_backup_file",
                column: "sha256");

            migrationBuilder.CreateIndex(
                name: "save_backup_file_snapshot_idx",
                table: "save_backup_file",
                column: "snapshot_id");

            migrationBuilder.CreateIndex(
                name: "save_backup_snapshot_target_idx",
                table: "save_backup_snapshot",
                columns: new[] { "target_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "save_backup_target_app_idx",
                table: "save_backup_target",
                column: "app_id");

            migrationBuilder.CreateIndex(
                name: "save_backup_target_path_uidx",
                table: "save_backup_target",
                column: "path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "screenshot_archive_taken_idx",
                table: "screenshot_archive",
                column: "taken_at");

            migrationBuilder.CreateIndex(
                name: "screenshot_archive_uidx",
                table: "screenshot_archive",
                columns: new[] { "account_id", "app_id", "file_name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "profile_history");

            migrationBuilder.DropTable(
                name: "save_backup_file");

            migrationBuilder.DropTable(
                name: "save_backup_snapshot");

            migrationBuilder.DropTable(
                name: "save_backup_target");

            migrationBuilder.DropTable(
                name: "screenshot_archive");
        }
    }
}
