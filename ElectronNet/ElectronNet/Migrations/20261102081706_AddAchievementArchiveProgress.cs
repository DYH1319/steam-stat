using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElectronNet.Migrations
{
    /// <inheritdoc />
    public partial class AddAchievementArchiveProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "progress_total",
                table: "achievement_game_archive",
                type: "INTEGER",
                nullable: true,
                comment: "归档时概览成就总数");

            migrationBuilder.AddColumn<int>(
                name: "progress_unlocked",
                table: "achievement_game_archive",
                type: "INTEGER",
                nullable: true,
                comment: "归档时概览已解锁数");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "progress_total",
                table: "achievement_game_archive");

            migrationBuilder.DropColumn(
                name: "progress_unlocked",
                table: "achievement_game_archive");
        }
    }
}
