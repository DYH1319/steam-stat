using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElectronNet.Migrations
{
    /// <inheritdoc />
    public partial class AddNintendo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "nintendo_account",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    na_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "任天堂账号 ID"),
                    nickname = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, comment: "昵称"),
                    icon_url = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true, comment: "头像地址"),
                    country = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true, comment: "地区"),
                    mine = table.Column<bool>(type: "INTEGER", nullable: false, comment: "属于用户本人"),
                    linked_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "绑定时间（Unix 时间戳，秒）"),
                    last_sync_at = table.Column<long>(type: "INTEGER", nullable: true, comment: "本机最近一次读取时间"),
                    last_success_at = table.Column<long>(type: "INTEGER", nullable: true, comment: "本机最近一次读取成功的时间"),
                    last_error = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true, comment: "本机最近一次读取的错误码")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nintendo_account", x => x.id);
                },
                comment: "绑定过的任天堂账号");

            migrationBuilder.CreateTable(
                name: "nintendo_credential",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    na_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "任天堂账号 ID"),
                    session_token = table.Column<string>(type: "TEXT", nullable: false, comment: "加密后的 session token"),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "写入时间（Unix 时间戳，秒）")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nintendo_credential", x => x.id);
                },
                comment: "任天堂账号的登录凭证（加密，只属于本机）");

            migrationBuilder.CreateTable(
                name: "nintendo_daily_playtime",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    na_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "任天堂账号 ID"),
                    played_day = table.Column<int>(type: "INTEGER", nullable: false, comment: "日期（距 1970-01-01 的天数，任天堂那边的日期）"),
                    title_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "标题 ID"),
                    platform = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, comment: "主机"),
                    minutes = table.Column<int>(type: "INTEGER", nullable: false, comment: "分钟数"),
                    observed_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "读取时间（Unix 时间戳，秒）")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nintendo_daily_playtime", x => x.id);
                },
                comment: "任天堂给的日级时长");

            migrationBuilder.CreateTable(
                name: "nintendo_playtime_snapshot",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    na_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "任天堂账号 ID"),
                    title_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "标题 ID"),
                    platform = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, comment: "主机"),
                    total_minutes = table.Column<int>(type: "INTEGER", nullable: false, comment: "累计分钟数"),
                    first_observed_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "首次读到该值的时间（Unix 时间戳，秒）"),
                    last_observed_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "最近一次读到该值的时间（Unix 时间戳，秒）"),
                    last_played_at = table.Column<long>(type: "INTEGER", nullable: true, comment: "读到该值时任天堂给的最近游玩时间")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nintendo_playtime_snapshot", x => x.id);
                },
                comment: "Nintendo 累计时长快照（按值变化分段）");

            migrationBuilder.CreateTable(
                name: "nintendo_title",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    title_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "标题 ID"),
                    platform = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, comment: "主机（switch / switch2 / other）"),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false, comment: "名称"),
                    image_url = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true, comment: "封面地址"),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "更新时间（Unix 时间戳，秒）")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nintendo_title", x => x.id);
                },
                comment: "Nintendo 游戏名称和封面");

            migrationBuilder.CreateTable(
                name: "nintendo_title_total",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    na_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "任天堂账号 ID"),
                    title_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "标题 ID"),
                    platform = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, comment: "主机"),
                    total_minutes = table.Column<int>(type: "INTEGER", nullable: false, comment: "累计分钟数"),
                    total_days = table.Column<int>(type: "INTEGER", nullable: false, comment: "游玩天数"),
                    first_played_at = table.Column<long>(type: "INTEGER", nullable: true, comment: "首次游玩时间（Unix 时间戳，秒）"),
                    last_played_at = table.Column<long>(type: "INTEGER", nullable: true, comment: "最近游玩时间（Unix 时间戳，秒）"),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "更新时间（Unix 时间戳，秒）")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nintendo_title_total", x => x.id);
                },
                comment: "任天堂给的每款游戏的累计值");

            migrationBuilder.CreateIndex(
                name: "nintendo_account_na_id_uidx",
                table: "nintendo_account",
                column: "na_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "nintendo_credential_na_id_uidx",
                table: "nintendo_credential",
                column: "na_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "nintendo_daily_playtime_day_idx",
                table: "nintendo_daily_playtime",
                column: "played_day");

            migrationBuilder.CreateIndex(
                name: "nintendo_daily_playtime_uidx",
                table: "nintendo_daily_playtime",
                columns: new[] { "na_id", "played_day", "title_id", "platform" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "nintendo_playtime_snapshot_first_idx",
                table: "nintendo_playtime_snapshot",
                column: "first_observed_at");

            migrationBuilder.CreateIndex(
                name: "nintendo_playtime_snapshot_uidx",
                table: "nintendo_playtime_snapshot",
                columns: new[] { "na_id", "title_id", "platform", "total_minutes" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "nintendo_title_uidx",
                table: "nintendo_title",
                columns: new[] { "title_id", "platform" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "nintendo_title_total_uidx",
                table: "nintendo_title_total",
                columns: new[] { "na_id", "title_id", "platform" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "nintendo_account");

            migrationBuilder.DropTable(
                name: "nintendo_credential");

            migrationBuilder.DropTable(
                name: "nintendo_daily_playtime");

            migrationBuilder.DropTable(
                name: "nintendo_playtime_snapshot");

            migrationBuilder.DropTable(
                name: "nintendo_title");

            migrationBuilder.DropTable(
                name: "nintendo_title_total");
        }
    }
}
