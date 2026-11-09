using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElectronNet.Migrations
{
    /// <inheritdoc />
    public partial class AddMemoir : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cloud_save_trace",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    steam_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "账号 SteamID"),
                    app_id = table.Column<int>(type: "INTEGER", nullable: false, comment: "应用 ID"),
                    file_key = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, comment: "文件相对路径的哈希"),
                    written_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "写入时间（Unix 时间戳，秒）"),
                    observed_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "观测时间（Unix 时间戳，秒）")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cloud_save_trace", x => x.id);
                },
                comment: "云存档写入时间，重建史前游玩日历的痕迹");

            migrationBuilder.CreateTable(
                name: "life_event",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    title = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false, comment: "标题"),
                    note = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true, comment: "备注"),
                    kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, comment: "类型"),
                    start_day = table.Column<int>(type: "INTEGER", nullable: false, comment: "开始的逻辑日（距 1970-01-01 的天数）"),
                    end_day = table.Column<int>(type: "INTEGER", nullable: true, comment: "结束的逻辑日，单日事件为空"),
                    boundary = table.Column<bool>(type: "INTEGER", nullable: false, comment: "从这里开始新的一章"),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "创建时间（Unix 时间戳，秒）"),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "修改时间（Unix 时间戳，秒）"),
                    sync_uid = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true, comment: "多设备同步 ID")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_life_event", x => x.id);
                },
                comment: "用户标注的人生事件");

            migrationBuilder.CreateTable(
                name: "time_capsule",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    label = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true, comment: "封存时可见的标签"),
                    message = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false, comment: "正文"),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "封存时间（Unix 时间戳，秒）"),
                    open_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "最早开启时间（Unix 时间戳，秒）"),
                    app_id = table.Column<int>(type: "INTEGER", nullable: true, comment: "绑定的游戏：过了日期再启动它才开"),
                    ready_at = table.Column<long>(type: "INTEGER", nullable: true, comment: "条件满足的时间"),
                    opened_at = table.Column<long>(type: "INTEGER", nullable: true, comment: "开启时间"),
                    context = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true, comment: "封存时的档案摘要（JSON）"),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "修改时间（Unix 时间戳，秒）"),
                    sync_uid = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true, comment: "多设备同步 ID")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_time_capsule", x => x.id);
                },
                comment: "时光胶囊：到期前正文不发给渲染层");

            migrationBuilder.CreateTable(
                name: "year_in_review",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    steam_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "账号 SteamID"),
                    year = table.Column<int>(type: "INTEGER", nullable: false, comment: "年份"),
                    fetched_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "读取时间（Unix 时间戳，秒）"),
                    result = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "EResult"),
                    payload = table.Column<byte[]>(type: "BLOB", nullable: true, comment: "原始 protobuf")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_year_in_review", x => x.id);
                },
                comment: "Steam 年度回顾的原始回复");

            migrationBuilder.CreateIndex(
                name: "cloud_save_trace_time_idx",
                table: "cloud_save_trace",
                column: "written_at");

            migrationBuilder.CreateIndex(
                name: "cloud_save_trace_uidx",
                table: "cloud_save_trace",
                columns: new[] { "steam_id", "app_id", "file_key", "written_at" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "life_event_start_idx",
                table: "life_event",
                column: "start_day");

            migrationBuilder.CreateIndex(
                name: "life_event_sync_uid_uidx",
                table: "life_event",
                column: "sync_uid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "time_capsule_open_idx",
                table: "time_capsule",
                column: "open_at");

            migrationBuilder.CreateIndex(
                name: "time_capsule_sync_uid_uidx",
                table: "time_capsule",
                column: "sync_uid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "year_in_review_uidx",
                table: "year_in_review",
                columns: new[] { "steam_id", "year" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cloud_save_trace");

            migrationBuilder.DropTable(
                name: "life_event");

            migrationBuilder.DropTable(
                name: "time_capsule");

            migrationBuilder.DropTable(
                name: "year_in_review");
        }
    }
}
