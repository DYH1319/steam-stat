using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElectronNet.Migrations
{
    /// <inheritdoc />
    public partial class FixNoIndexBug : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "use_app_record_app_id_idx",
                table: "use_app_record",
                column: "app_id");

            migrationBuilder.CreateIndex(
                name: "use_app_record_start_time_idx",
                table: "use_app_record",
                column: "start_time");

            migrationBuilder.CreateIndex(
                name: "use_app_record_steam_id_app_id_idx",
                table: "use_app_record",
                columns: new[] { "steam_id", "app_id" });

            migrationBuilder.CreateIndex(
                name: "use_app_record_steam_id_idx",
                table: "use_app_record",
                column: "steam_id");

            migrationBuilder.CreateIndex(
                name: "IX_steam_user_account_id",
                table: "steam_user",
                column: "account_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_steam_user_steam_id",
                table: "steam_user",
                column: "steam_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_steam_app_app_id",
                table: "steam_app",
                column: "app_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "steam_app_installed_idx",
                table: "steam_app",
                column: "installed");

            migrationBuilder.CreateIndex(
                name: "steam_app_name_idx",
                table: "steam_app",
                column: "name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
