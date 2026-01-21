using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElectronNet.Migrations
{
    /// <inheritdoc />
    public partial class ChangeBlobToIntegerInSteamUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "steam_id",
                table: "steam_user",
                type: "INTEGER",
                nullable: false,
                comment: "Steam ID",
                oldClrType: typeof(long),
                oldType: "BLOB",
                oldComment: "Steam ID");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "steam_id",
                table: "steam_user",
                type: "BLOB",
                nullable: false,
                comment: "Steam ID",
                oldClrType: typeof(long),
                oldType: "INTEGER",
                oldComment: "Steam ID");
        }
    }
}
