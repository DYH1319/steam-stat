using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElectronNet.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaytimeHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "import_batch_id",
                table: "use_app_record",
                type: "INTEGER",
                nullable: true,
                comment: "导入批次 ID（CSV 导入的会话）");

            migrationBuilder.CreateTable(
                name: "playtime_snapshot",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    steam_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, comment: "Steam ID"),
                    app_id = table.Column<int>(type: "INTEGER", nullable: false, comment: "App ID"),
                    playtime_minutes = table.Column<int>(type: "INTEGER", nullable: false, comment: "Steam 官方累计时长，分钟"),
                    source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, comment: "数据来源（localconfig | cm）"),
                    first_observed_at = table.Column<int>(type: "INTEGER", nullable: false, comment: "首次观测到该值的 Unix 时间戳"),
                    last_observed_at = table.Column<int>(type: "INTEGER", nullable: false, comment: "最近一次观测到该值的 Unix 时间戳"),
                    last_played_at = table.Column<int>(type: "INTEGER", nullable: true, comment: "Steam 记录的最后游玩 Unix 时间戳")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_playtime_snapshot", x => x.id);
                },
                comment: "Steam 官方累计时长快照表（按值变化分段）");

            migrationBuilder.CreateTable(
                name: "session_import_batch",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    file_name = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false, comment: "导入文件名"),
                    created_at = table.Column<int>(type: "INTEGER", nullable: false, comment: "导入时间的 Unix 时间戳"),
                    row_count = table.Column<int>(type: "INTEGER", nullable: false, comment: "导入的会话条数")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_import_batch", x => x.id);
                },
                comment: "会话导入批次表");

            migrationBuilder.CreateIndex(
                name: "use_app_record_import_batch_id_idx",
                table: "use_app_record",
                column: "import_batch_id");

            migrationBuilder.CreateIndex(
                name: "playtime_snapshot_steam_id_app_id_first_idx",
                table: "playtime_snapshot",
                columns: new[] { "steam_id", "app_id", "first_observed_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "playtime_snapshot");

            migrationBuilder.DropTable(
                name: "session_import_batch");

            migrationBuilder.DropIndex(
                name: "use_app_record_import_batch_id_idx",
                table: "use_app_record");

            migrationBuilder.DropColumn(
                name: "import_batch_id",
                table: "use_app_record");
        }
    }
}
