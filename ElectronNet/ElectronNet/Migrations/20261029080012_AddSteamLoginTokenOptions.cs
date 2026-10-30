using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElectronNet.Migrations
{
    /// <inheritdoc />
    public partial class AddSteamLoginTokenOptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "auto_login_enabled",
                table: "steam_login_token",
                type: "INTEGER",
                nullable: false,
                defaultValue: false,
                comment: "是否随应用启动自动登录");

            migrationBuilder.AddColumn<int>(
                name: "login_persona_state",
                table: "steam_login_token",
                type: "INTEGER",
                nullable: true,
                comment: "登录后设置的 Steam 在线状态（EPersonaState，null 表示不设置）");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "auto_login_enabled",
                table: "steam_login_token");

            migrationBuilder.DropColumn(
                name: "login_persona_state",
                table: "steam_login_token");
        }
    }
}
