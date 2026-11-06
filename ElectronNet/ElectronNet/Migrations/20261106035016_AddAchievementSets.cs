using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElectronNet.Migrations
{
    /// <inheritdoc />
    public partial class AddAchievementSets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "archived",
                table: "achievement_unlock_archive",
                type: "INTEGER",
                nullable: false,
                defaultValue: false,
                comment: "成就或所在的集是否已归档");

            migrationBuilder.AddColumn<long>(
                name: "group_id",
                table: "achievement_unlock_archive",
                type: "INTEGER",
                nullable: true,
                comment: "所在成就集 ID");

            migrationBuilder.AddColumn<int>(
                name: "all_total",
                table: "achievement_game_archive",
                type: "INTEGER",
                nullable: true,
                comment: "全部成就数，含未拥有的 DLC 和归档成就");

            migrationBuilder.AddColumn<int>(
                name: "all_unlocked",
                table: "achievement_game_archive",
                type: "INTEGER",
                nullable: true,
                comment: "全部成就里的已解锁数");

            migrationBuilder.CreateTable(
                name: "achievement_schema_change",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    app_id = table.Column<long>(type: "INTEGER", nullable: false, comment: "应用 ID"),
                    schema_hash = table.Column<long>(type: "INTEGER", nullable: false, comment: "变化后的 schema 哈希"),
                    internal_name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, comment: "成就内部名称"),
                    name = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false, comment: "成就本地化名称"),
                    kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, comment: "变化类型"),
                    old_value = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true, comment: "旧值（集名或成就名）"),
                    new_value = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true, comment: "新值（集名或成就名）"),
                    detected_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "发现时间（Unix 时间戳，秒）")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_achievement_schema_change", x => x.id);
                },
                comment: "成就 schema 变更史");

            migrationBuilder.CreateTable(
                name: "achievement_schema_state",
                columns: table => new
                {
                    app_id = table.Column<long>(type: "INTEGER", nullable: false, comment: "应用 ID"),
                    schema_hash = table.Column<long>(type: "INTEGER", nullable: false, comment: "schema 哈希"),
                    language = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, comment: "成就名称的语言"),
                    items = table.Column<string>(type: "TEXT", nullable: false, comment: "每个成就的状态（JSON）"),
                    observed_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "观测时间（Unix 时间戳，秒）")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_achievement_schema_state", x => x.app_id);
                },
                comment: "最近一次观测到的成就 schema");

            migrationBuilder.CreateTable(
                name: "achievement_set_archive",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    account_name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, comment: "登录用户账户名"),
                    steam_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, comment: "账号 Steam ID"),
                    app_id = table.Column<long>(type: "INTEGER", nullable: false, comment: "应用 ID"),
                    group_id = table.Column<long>(type: "INTEGER", nullable: false, comment: "成就集 ID"),
                    name = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false, comment: "成就集本地化名称，基础集为空"),
                    dlc_app_id = table.Column<long>(type: "INTEGER", nullable: true, comment: "DLC 应用 ID"),
                    archived = table.Column<bool>(type: "INTEGER", nullable: false, comment: "成就集是否已归档"),
                    counted = table.Column<bool>(type: "INTEGER", nullable: false, comment: "是否计入完成度"),
                    owned = table.Column<bool>(type: "INTEGER", nullable: true, comment: "DLC 是否拥有，基础集和无法判断时为空"),
                    total = table.Column<int>(type: "INTEGER", nullable: false, comment: "对这名玩家的成就数，不含未获得的归档成就"),
                    unlocked = table.Column<int>(type: "INTEGER", nullable: false, comment: "已解锁数"),
                    all_total = table.Column<int>(type: "INTEGER", nullable: false, comment: "集内全部成就数"),
                    completed = table.Column<bool>(type: "INTEGER", nullable: false, comment: "是否已完成，完成后不再清除"),
                    completed_at = table.Column<long>(type: "INTEGER", nullable: true, comment: "完成时间，取集内最后一个解锁时间（Unix 时间戳，秒）"),
                    synced_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "最近归档时间（Unix 时间戳，秒）")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_achievement_set_archive", x => x.id);
                },
                comment: "成就集进度归档表");

            migrationBuilder.CreateIndex(
                name: "achievement_schema_change_uidx",
                table: "achievement_schema_change",
                columns: new[] { "app_id", "internal_name", "kind", "schema_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "achievement_set_archive_uidx",
                table: "achievement_set_archive",
                columns: new[] { "account_name", "app_id", "group_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "achievement_schema_change");

            migrationBuilder.DropTable(
                name: "achievement_schema_state");

            migrationBuilder.DropTable(
                name: "achievement_set_archive");

            migrationBuilder.DropColumn(
                name: "archived",
                table: "achievement_unlock_archive");

            migrationBuilder.DropColumn(
                name: "group_id",
                table: "achievement_unlock_archive");

            migrationBuilder.DropColumn(
                name: "all_total",
                table: "achievement_game_archive");

            migrationBuilder.DropColumn(
                name: "all_unlocked",
                table: "achievement_game_archive");
        }
    }
}
