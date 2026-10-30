using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElectronNet.Migrations
{
    /// <inheritdoc />
    public partial class AddFriendTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "friend_tracking",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    account_name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, comment: "登录用户账户名"),
                    friend_steam_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, comment: "被追踪好友的 Steam ID"),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "开始追踪时间（Unix 时间戳，秒）")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_friend_tracking", x => x.id);
                },
                comment: "好友追踪名单表");

            migrationBuilder.CreateIndex(
                name: "friend_tracking_account_friend_uidx",
                table: "friend_tracking",
                columns: new[] { "account_name", "friend_steam_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "friend_tracking");
        }
    }
}
