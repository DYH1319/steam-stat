using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElectronNet.Migrations
{
    /// <inheritdoc />
    public partial class AddRichPresenceRecord : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rich_presence_record",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    account_name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, comment: "观察到这条状态的本机账号"),
                    steam_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "状态所属的玩家"),
                    is_self = table.Column<bool>(type: "INTEGER", nullable: false, comment: "是否账号本人"),
                    app_id = table.Column<int>(type: "INTEGER", nullable: false, comment: "应用 ID"),
                    raw_values = table.Column<string>(type: "TEXT", nullable: false, comment: "Steam 给出的原始键值（JSON）"),
                    display_text = table.Column<string>(type: "TEXT", nullable: true, comment: "解析后的状态文案"),
                    source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, comment: "来源：push / poll"),
                    observed_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "观测时间的 Unix 时间戳")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rich_presence_record", x => x.id);
                },
                comment: "游戏内状态变化记录，保留原始键值");

            migrationBuilder.CreateIndex(
                name: "rich_presence_record_app_id_observed_at_idx",
                table: "rich_presence_record",
                columns: new[] { "app_id", "observed_at" });

            migrationBuilder.CreateIndex(
                name: "rich_presence_record_steam_id_observed_at_idx",
                table: "rich_presence_record",
                columns: new[] { "steam_id", "observed_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rich_presence_record");
        }
    }
}
