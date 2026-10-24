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
            migrationBuilder.AlterColumn<string>(
                name: "source",
                table: "use_app_record",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "auto",
                comment: "记录来源（auto | merged | imported）",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 16,
                oldDefaultValue: "auto",
                oldComment: "记录来源（auto | merged）");

            migrationBuilder.AddColumn<string>(
                name: "import_batch_id",
                table: "use_app_record",
                type: "TEXT",
                maxLength: 32,
                nullable: true,
                comment: "CSV 导入批次 ID（session_import_batch.id）");

            migrationBuilder.CreateTable(
                name: "playtime_snapshot",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    steam_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, comment: "Steam ID"),
                    app_id = table.Column<int>(type: "INTEGER", nullable: false, comment: "App ID"),
                    source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, comment: "快照来源（localconfig | cm）"),
                    playtime_minutes = table.Column<int>(type: "INTEGER", nullable: false, comment: "Steam 官方累计时长（分钟）"),
                    captured_at = table.Column<int>(type: "INTEGER", nullable: false, comment: "首次观察到该值的 Unix 时间戳"),
                    last_seen_at = table.Column<int>(type: "INTEGER", nullable: false, comment: "最后一次仍观察到该值未变化的 Unix 时间戳"),
                    last_played = table.Column<int>(type: "INTEGER", nullable: true, comment: "官方最后游玩 Unix 时间戳（0 存为 null）"),
                    is_baseline = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false, comment: "是否为该组对账基线")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_playtime_snapshot", x => x.id);
                },
                comment: "Steam 官方累计时长快照（localconfig / CM），用于历史导入与对账");

            migrationBuilder.CreateTable(
                name: "session_import_batch",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "批次 ID（Guid，32 位无连字符）"),
                    file_name = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false, comment: "导入文件名"),
                    imported_at = table.Column<int>(type: "INTEGER", nullable: false, comment: "导入的 Unix 时间戳"),
                    row_count = table.Column<int>(type: "INTEGER", nullable: false, comment: "导入的会话条数")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_import_batch", x => x.id);
                },
                comment: "CSV 会话导入批次");

            migrationBuilder.CreateIndex(
                name: "use_app_record_import_batch_id_idx",
                table: "use_app_record",
                column: "import_batch_id");

            migrationBuilder.CreateIndex(
                name: "playtime_snapshot_lookup_idx",
                table: "playtime_snapshot",
                columns: new[] { "steam_id", "app_id", "source", "captured_at" });
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

            migrationBuilder.AlterColumn<string>(
                name: "source",
                table: "use_app_record",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "auto",
                comment: "记录来源（auto | merged）",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 16,
                oldDefaultValue: "auto",
                oldComment: "记录来源（auto | merged | imported）");
        }
    }
}
