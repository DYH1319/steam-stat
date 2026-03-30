using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElectronNet.Migrations
{
    /// <inheritdoc />
    public partial class AddSteamLoginToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "steam_login_token",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    account_name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, comment: "账号名"),
                    access_token = table.Column<string>(type: "TEXT", maxLength: 2147483647, nullable: false, comment: "Access Token (JWT)"),
                    refresh_token = table.Column<string>(type: "TEXT", maxLength: 2147483647, nullable: false, comment: "Refresh Token (JWT)"),
                    guard_data = table.Column<string>(type: "TEXT", maxLength: 2147483647, nullable: true, comment: "Steam Guard 数据"),
                    created_at = table.Column<int>(type: "INTEGER", nullable: false, comment: "创建时间 Unix 时间戳")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_steam_login_token", x => x.id);
                },
                comment: "Steam 登录 Token 表");

            migrationBuilder.CreateIndex(
                name: "IX_steam_login_token_account_name",
                table: "steam_login_token",
                column: "account_name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "steam_login_token");
        }
    }
}
