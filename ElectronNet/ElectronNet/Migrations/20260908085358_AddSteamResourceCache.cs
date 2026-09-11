using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElectronNet.Migrations
{
    /// <inheritdoc />
    public partial class AddSteamResourceCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "steam_resource_cache",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    resource_kind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    scope_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    resource_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    language = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, defaultValue: ""),
                    variant = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, defaultValue: ""),
                    schema_version = table.Column<int>(type: "INTEGER", nullable: false),
                    payload_format = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    payload = table.Column<string>(type: "TEXT", maxLength: 2147483647, nullable: false),
                    source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    etag = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    content_hash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    fetched_at = table.Column<long>(type: "INTEGER", nullable: false),
                    refresh_after = table.Column<long>(type: "INTEGER", nullable: false),
                    retain_until = table.Column<long>(type: "INTEGER", nullable: true),
                    last_accessed_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_steam_resource_cache", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "steam_resource_cache_key_idx",
                table: "steam_resource_cache",
                columns: new[] { "resource_kind", "scope_id", "resource_id", "language", "variant", "schema_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "steam_resource_cache_retain_until_idx",
                table: "steam_resource_cache",
                column: "retain_until");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "steam_resource_cache");
        }
    }
}
