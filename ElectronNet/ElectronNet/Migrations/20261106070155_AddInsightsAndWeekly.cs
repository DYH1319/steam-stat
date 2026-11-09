using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElectronNet.Migrations
{
    /// <inheritdoc />
    public partial class AddInsightsAndWeekly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "auto_tags",
                table: "use_app_record",
                type: "INTEGER",
                nullable: true,
                comment: "会话自动标签（位标记），为空表示还没打标");

            migrationBuilder.AddColumn<int>(
                name: "build_id",
                table: "use_app_record",
                type: "INTEGER",
                nullable: true,
                comment: "开始时本机观测到的游戏版本");

            migrationBuilder.CreateTable(
                name: "app_build_history",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    app_id = table.Column<int>(type: "INTEGER", nullable: false, comment: "应用 ID"),
                    build_id = table.Column<int>(type: "INTEGER", nullable: false, comment: "版本号"),
                    observed_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "版本出现时间（Unix 时间戳，秒）"),
                    is_baseline = table.Column<bool>(type: "INTEGER", nullable: false, comment: "第一次看到这款游戏，不算更新")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_build_history", x => x.id);
                },
                comment: "游戏版本史（本机 ACF 观测）");

            migrationBuilder.CreateTable(
                name: "app_store_status",
                columns: table => new
                {
                    app_id = table.Column<int>(type: "INTEGER", nullable: false, comment: "应用 ID"),
                    visible = table.Column<bool>(type: "INTEGER", nullable: false, comment: "商店页是否可见"),
                    purchasable = table.Column<bool>(type: "INTEGER", nullable: false, comment: "是否有购买选项"),
                    is_free = table.Column<bool>(type: "INTEGER", nullable: false, comment: "是否免费"),
                    price_cents = table.Column<int>(type: "INTEGER", nullable: true, comment: "美区原价（美分）"),
                    review_count = table.Column<int>(type: "INTEGER", nullable: true, comment: "评测数"),
                    review_percent = table.Column<int>(type: "INTEGER", nullable: true, comment: "好评率（0~100）"),
                    franchise = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true, comment: "系列名"),
                    release_date = table.Column<long>(type: "INTEGER", nullable: true, comment: "发行日期（Unix 时间戳，秒）"),
                    delisted_at = table.Column<long>(type: "INTEGER", nullable: true, comment: "第一次发现下架的时间（Unix 时间戳，秒）"),
                    checked_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "上次检查时间（Unix 时间戳，秒）")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_store_status", x => x.app_id);
                },
                comment: "游戏的商店公开状态（美区）");

            migrationBuilder.CreateTable(
                name: "weekly_issue",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, comment: "weekly / extra"),
                    issue_key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, comment: "去重键"),
                    number = table.Column<int>(type: "INTEGER", nullable: false, comment: "期号"),
                    from_day = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false, comment: "起始逻辑日"),
                    to_day = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false, comment: "结束逻辑日"),
                    generated_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "生成时间（Unix 时间戳，秒）"),
                    read_at = table.Column<long>(type: "INTEGER", nullable: true, comment: "首次打开时间（Unix 时间戳，秒）"),
                    backfilled = table.Column<bool>(type: "INTEGER", nullable: false, comment: "补印：不是当时生成的"),
                    payload = table.Column<string>(type: "TEXT", nullable: false, comment: "冻结的完整内容（JSON）")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_weekly_issue", x => x.id);
                },
                comment: "档案周刊，生成后冻结");

            migrationBuilder.CreateIndex(
                name: "app_build_history_app_idx",
                table: "app_build_history",
                columns: new[] { "app_id", "observed_at" });

            migrationBuilder.CreateIndex(
                name: "weekly_issue_generated_idx",
                table: "weekly_issue",
                column: "generated_at");

            migrationBuilder.CreateIndex(
                name: "weekly_issue_key_uidx",
                table: "weekly_issue",
                columns: new[] { "kind", "issue_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_build_history");

            migrationBuilder.DropTable(
                name: "app_store_status");

            migrationBuilder.DropTable(
                name: "weekly_issue");

            migrationBuilder.DropColumn(
                name: "auto_tags",
                table: "use_app_record");

            migrationBuilder.DropColumn(
                name: "build_id",
                table: "use_app_record");
        }
    }
}
