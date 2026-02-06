using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElectronNet.Migrations
{
    /// <inheritdoc />
    public partial class RefactorSteamAppRefreshTime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "refresh_time",
                table: "steam_app");

            migrationBuilder.AddColumn<int>(
                name: "steam_app_refresh_time",
                table: "global_status",
                type: "INTEGER",
                nullable: true,
                comment: "steamApp 表刷新时间");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "steam_app_refresh_time",
                table: "global_status");

            migrationBuilder.AddColumn<int>(
                name: "refresh_time",
                table: "steam_app",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                comment: "刷新时间");
        }
    }
}
