using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElectronNet.Migrations
{
    /// <inheritdoc />
    public partial class AddSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "sync_uid",
                table: "use_app_record",
                type: "TEXT",
                maxLength: 32,
                nullable: true,
                comment: "多设备同步 ID");

            migrationBuilder.AddColumn<string>(
                name: "sync_uid",
                table: "profile_history",
                type: "TEXT",
                maxLength: 32,
                nullable: true,
                comment: "多设备同步 ID");

            migrationBuilder.CreateTable(
                name: "sync_control",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "固定为 1"),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false, comment: "是否启用同步（触发器据此记录变更）"),
                    applying = table.Column<bool>(type: "INTEGER", nullable: false, comment: "正在应用远端变更（触发器据此跳过）"),
                    vault_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true, comment: "远端保险库 ID"),
                    device_seq = table.Column<long>(type: "INTEGER", nullable: false, comment: "本机已写出的最大段序号"),
                    full_export_done = table.Column<bool>(type: "INTEGER", nullable: false, comment: "本机全量快照是否已上传"),
                    last_sync_at = table.Column<long>(type: "INTEGER", nullable: true, comment: "最近一次成功同步（Unix 秒）"),
                    last_error = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true, comment: "最近一次同步错误码")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_control", x => x.id);
                },
                comment: "多设备同步的本机状态（单行）");

            migrationBuilder.CreateTable(
                name: "sync_cursor",
                columns: table => new
                {
                    device_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, comment: "远端设备 ID"),
                    last_seq = table.Column<long>(type: "INTEGER", nullable: false, comment: "已应用的最大段序号"),
                    device_name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true, comment: "设备名"),
                    app_version = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true, comment: "应用版本"),
                    platform = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true, comment: "平台"),
                    remote_updated_at = table.Column<long>(type: "INTEGER", nullable: true, comment: "远端设备最近上传（Unix 秒）"),
                    applied_at = table.Column<long>(type: "INTEGER", nullable: true, comment: "最近一次应用（Unix 秒）")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_cursor", x => x.device_id);
                },
                comment: "已应用的远端设备进度");

            migrationBuilder.CreateTable(
                name: "sync_outbox",
                columns: table => new
                {
                    seq = table.Column<long>(type: "INTEGER", nullable: false, comment: "自增序号")
                        .Annotation("Sqlite:Autoincrement", true),
                    table_name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, comment: "表名"),
                    row_id = table.Column<long>(type: "INTEGER", nullable: false, comment: "行 rowid"),
                    sync_key = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true, comment: "删除时的同步键"),
                    op = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false, comment: "upsert | delete"),
                    changed_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "变更时间（Unix 毫秒）")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_outbox", x => x.seq);
                },
                comment: "待上传的本机行变更（触发器写入）");

            migrationBuilder.CreateTable(
                name: "sync_version",
                columns: table => new
                {
                    table_name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, comment: "表名"),
                    sync_key = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false, comment: "同步键"),
                    changed_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "版本时间（Unix 毫秒）"),
                    device_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, comment: "写出该版本的设备"),
                    deleted = table.Column<bool>(type: "INTEGER", nullable: false, comment: "是否为删除墓碑")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_version", x => new { x.table_name, x.sync_key });
                },
                comment: "同步行版本（最后写入胜出，删除保留为墓碑）");

            migrationBuilder.CreateIndex(
                name: "use_app_record_sync_uid_uidx",
                table: "use_app_record",
                column: "sync_uid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "profile_history_sync_uid_uidx",
                table: "profile_history",
                column: "sync_uid",
                unique: true);

            // 已有记录补齐同步 ID（之后由 SyncSchema 安装的触发器在插入时生成）
            migrationBuilder.Sql("UPDATE use_app_record SET sync_uid = lower(hex(randomblob(16))) WHERE sync_uid IS NULL;");
            migrationBuilder.Sql("UPDATE profile_history SET sync_uid = lower(hex(randomblob(16))) WHERE sync_uid IS NULL;");
            migrationBuilder.Sql("INSERT OR IGNORE INTO sync_control (id, enabled, applying, device_seq, full_export_done) VALUES (1, 0, 0, 0, 0);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sync_control");

            migrationBuilder.DropTable(
                name: "sync_cursor");

            migrationBuilder.DropTable(
                name: "sync_outbox");

            migrationBuilder.DropTable(
                name: "sync_version");

            migrationBuilder.DropIndex(
                name: "use_app_record_sync_uid_uidx",
                table: "use_app_record");

            migrationBuilder.DropIndex(
                name: "profile_history_sync_uid_uidx",
                table: "profile_history");

            migrationBuilder.DropColumn(
                name: "sync_uid",
                table: "use_app_record");

            migrationBuilder.DropColumn(
                name: "sync_uid",
                table: "profile_history");
        }
    }
}
