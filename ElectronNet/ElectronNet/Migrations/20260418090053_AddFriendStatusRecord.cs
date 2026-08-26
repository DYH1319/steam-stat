using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElectronNet.Migrations
{
    /// <inheritdoc />
    public partial class AddFriendStatusRecord : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "friend_status_record",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    account_name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, comment: "登录用户账户名"),
                    friend_steam_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, comment: "被记录好友的 Steam ID"),
                    friend_persona_name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, comment: "好友昵称（变化时快照）"),
                    change_type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "变化类型：state / game / personaName"),
                    previous_value = table.Column<string>(type: "TEXT", maxLength: 2147483647, nullable: true, comment: "变化前的值（JSON 字符串）"),
                    current_value = table.Column<string>(type: "TEXT", maxLength: 2147483647, nullable: true, comment: "变化后的值（JSON 字符串）"),
                    timestamp = table.Column<long>(type: "INTEGER", nullable: false, comment: "变化发生时间（Unix 时间戳，秒）")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_friend_status_record", x => x.id);
                },
                comment: "好友状态变化记录表");

            migrationBuilder.CreateIndex(
                name: "friend_status_record_account_friend_idx",
                table: "friend_status_record",
                columns: new[] { "account_name", "friend_steam_id" });

            migrationBuilder.CreateIndex(
                name: "friend_status_record_account_name_idx",
                table: "friend_status_record",
                column: "account_name");

            migrationBuilder.CreateIndex(
                name: "friend_status_record_friend_steam_id_idx",
                table: "friend_status_record",
                column: "friend_steam_id");

            migrationBuilder.CreateIndex(
                name: "friend_status_record_timestamp_idx",
                table: "friend_status_record",
                column: "timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "friend_status_record");
        }
    }
}
