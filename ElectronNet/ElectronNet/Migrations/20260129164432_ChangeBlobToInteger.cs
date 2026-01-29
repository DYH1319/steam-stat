using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElectronNet.Migrations
{
    /// <inheritdoc />
    public partial class ChangeBlobToInteger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "steam_id",
                table: "use_app_record",
                type: "INTEGER",
                nullable: false,
                comment: "使用 App 的 Steam ID",
                oldClrType: typeof(long),
                oldType: "BLOB",
                oldComment: "使用 App 的 Steam ID");

            migrationBuilder.AlterColumn<long>(
                name: "app_on_disk_real",
                table: "steam_app",
                type: "INTEGER",
                nullable: true,
                comment: "应用文件真实占用大小",
                oldClrType: typeof(long),
                oldType: "BLOB",
                oldNullable: true,
                oldComment: "应用文件真实占用大小");

            migrationBuilder.AlterColumn<long>(
                name: "app_on_disk",
                table: "steam_app",
                type: "INTEGER",
                nullable: true,
                comment: "应用文件占用大小",
                oldClrType: typeof(long),
                oldType: "BLOB",
                oldNullable: true,
                oldComment: "应用文件占用大小");

            migrationBuilder.AlterColumn<long>(
                name: "active_user_steam_id",
                table: "global_status",
                type: "INTEGER",
                nullable: true,
                comment: "当前登录用户的 Steam ID",
                oldClrType: typeof(long),
                oldType: "BLOB",
                oldNullable: true,
                oldComment: "当前登录用户的 Steam ID");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "steam_id",
                table: "use_app_record",
                type: "BLOB",
                nullable: false,
                comment: "使用 App 的 Steam ID",
                oldClrType: typeof(long),
                oldType: "INTEGER",
                oldComment: "使用 App 的 Steam ID");

            migrationBuilder.AlterColumn<long>(
                name: "app_on_disk_real",
                table: "steam_app",
                type: "BLOB",
                nullable: true,
                comment: "应用文件真实占用大小",
                oldClrType: typeof(long),
                oldType: "INTEGER",
                oldNullable: true,
                oldComment: "应用文件真实占用大小");

            migrationBuilder.AlterColumn<long>(
                name: "app_on_disk",
                table: "steam_app",
                type: "BLOB",
                nullable: true,
                comment: "应用文件占用大小",
                oldClrType: typeof(long),
                oldType: "INTEGER",
                oldNullable: true,
                oldComment: "应用文件占用大小");

            migrationBuilder.AlterColumn<long>(
                name: "active_user_steam_id",
                table: "global_status",
                type: "BLOB",
                nullable: true,
                comment: "当前登录用户的 Steam ID",
                oldClrType: typeof(long),
                oldType: "INTEGER",
                oldNullable: true,
                oldComment: "当前登录用户的 Steam ID");
        }
    }
}
