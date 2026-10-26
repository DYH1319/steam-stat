using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElectronNet.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "device_id",
                table: "use_app_record",
                type: "TEXT",
                maxLength: 64,
                nullable: true,
                comment: "采集设备 ID");

            migrationBuilder.AddColumn<string>(
                name: "end_reason",
                table: "use_app_record",
                type: "TEXT",
                maxLength: 16,
                nullable: true,
                comment: "结束原因（exited | suspended | recovered | ended | discarded）");

            migrationBuilder.AddColumn<long>(
                name: "foreground_duration",
                table: "use_app_record",
                type: "INTEGER",
                nullable: true,
                comment: "前台时长，秒（null 表示未知）");

            migrationBuilder.AddColumn<long>(
                name: "idle_duration",
                table: "use_app_record",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L,
                comment: "离开（AFK）时长，秒");

            migrationBuilder.AddColumn<int>(
                name: "last_heartbeat",
                table: "use_app_record",
                type: "INTEGER",
                nullable: true,
                comment: "最近一次心跳的 Unix 时间戳");

            migrationBuilder.AddColumn<string>(
                name: "note",
                table: "use_app_record",
                type: "TEXT",
                maxLength: 2000,
                nullable: true,
                comment: "用户备注");

            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "use_app_record",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "auto",
                comment: "记录来源（auto | merged）");

            migrationBuilder.AddColumn<string>(
                name: "tags",
                table: "use_app_record",
                type: "TEXT",
                nullable: true,
                comment: "标签（JSON 字符串数组）");

            migrationBuilder.AddColumn<int>(
                name: "updated_at",
                table: "use_app_record",
                type: "INTEGER",
                nullable: true,
                comment: "最近一次手动编辑/合并的 Unix 时间戳");

            migrationBuilder.CreateTable(
                name: "device",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, comment: "设备 ID（Guid）"),
                    name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, comment: "设备名称"),
                    is_current = table.Column<bool>(type: "INTEGER", nullable: false, comment: "是否为当前设备"),
                    created_at = table.Column<int>(type: "INTEGER", nullable: false, comment: "创建时间的 Unix 时间戳")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device", x => x.id);
                },
                comment: "采集设备表");

            migrationBuilder.CreateIndex(
                name: "use_app_record_device_id_idx",
                table: "use_app_record",
                column: "device_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device");

            migrationBuilder.DropIndex(
                name: "use_app_record_device_id_idx",
                table: "use_app_record");

            migrationBuilder.DropColumn(
                name: "device_id",
                table: "use_app_record");

            migrationBuilder.DropColumn(
                name: "end_reason",
                table: "use_app_record");

            migrationBuilder.DropColumn(
                name: "foreground_duration",
                table: "use_app_record");

            migrationBuilder.DropColumn(
                name: "idle_duration",
                table: "use_app_record");

            migrationBuilder.DropColumn(
                name: "last_heartbeat",
                table: "use_app_record");

            migrationBuilder.DropColumn(
                name: "note",
                table: "use_app_record");

            migrationBuilder.DropColumn(
                name: "source",
                table: "use_app_record");

            migrationBuilder.DropColumn(
                name: "tags",
                table: "use_app_record");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "use_app_record");
        }
    }
}
