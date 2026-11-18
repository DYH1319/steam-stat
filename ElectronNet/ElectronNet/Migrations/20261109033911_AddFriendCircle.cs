using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElectronNet.Migrations
{
    /// <inheritdoc />
    public partial class AddFriendCircle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "exchange_card",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    sender_steam_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "发卡人 SteamID"),
                    sender_name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, comment: "发卡人昵称"),
                    year = table.Column<int>(type: "INTEGER", nullable: false, comment: "卡上的年份"),
                    generated_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "生成时间（Unix 时间戳，秒）"),
                    imported_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "导入时间（Unix 时间戳，秒）"),
                    payload = table.Column<string>(type: "TEXT", nullable: false, comment: "卡的内容（JSON）")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_exchange_card", x => x.id);
                },
                comment: "导入的交换卡");

            migrationBuilder.CreateTable(
                name: "friend_achievement_progress",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    steam_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "好友 SteamID"),
                    app_id = table.Column<int>(type: "INTEGER", nullable: false, comment: "应用 ID"),
                    unlocked = table.Column<int>(type: "INTEGER", nullable: false, comment: "已解锁"),
                    total = table.Column<int>(type: "INTEGER", nullable: false, comment: "总数"),
                    observed_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "观测时间（Unix 时间戳，秒）")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_friend_achievement_progress", x => x.id);
                },
                comment: "手动追踪的好友的成就进度，值变化才写");

            migrationBuilder.CreateTable(
                name: "friend_library_item",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    steam_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "好友 SteamID"),
                    app_id = table.Column<int>(type: "INTEGER", nullable: false, comment: "应用 ID"),
                    first_seen_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "第一次看到（Unix 时间戳，秒）"),
                    is_baseline = table.Column<bool>(type: "INTEGER", nullable: false, comment: "第一次采集就有，入库时间不知道"),
                    removed_at = table.Column<long>(type: "INTEGER", nullable: true, comment: "从游戏库消失的时间")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_friend_library_item", x => x.id);
                },
                comment: "手动追踪的好友的游戏库，记录入库时间");

            migrationBuilder.CreateTable(
                name: "friend_observation_window",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    account_name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, comment: "本机账号"),
                    started_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "开始（Unix 时间戳，秒）"),
                    ended_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "结束或最后一次心跳（Unix 时间戳，秒）")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_friend_observation_window", x => x.id);
                },
                comment: "应用能看到好友状态的时间段");

            migrationBuilder.CreateTable(
                name: "friend_since",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    account_name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, comment: "本机账号"),
                    friend_steam_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "好友 SteamID"),
                    friend_since = table.Column<long>(type: "INTEGER", nullable: false, comment: "成为好友的时间（Unix 时间戳，秒）"),
                    observed_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "读取时间（Unix 时间戳，秒）")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_friend_since", x => x.id);
                },
                comment: "Steam 给的成为好友的时间");

            migrationBuilder.CreateTable(
                name: "presence_group_record",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    account_name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, comment: "观察到的本机账号"),
                    steam_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "玩家 SteamID，可以是账号本人"),
                    is_self = table.Column<bool>(type: "INTEGER", nullable: false, comment: "是否账号本人"),
                    app_id = table.Column<int>(type: "INTEGER", nullable: false, comment: "应用 ID"),
                    kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, comment: "分组类型：lobby / server"),
                    group_key = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "分组的哈希，空表示离开"),
                    observed_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "观测时间（Unix 时间戳，秒）")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_presence_group_record", x => x.id);
                },
                comment: "大厅和服务器的变化，用于判定是否在同一局");

            migrationBuilder.CreateTable(
                name: "together_record",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    session_id = table.Column<long>(type: "INTEGER", nullable: false, comment: "use_app_record.id"),
                    steam_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "账号 SteamID"),
                    friend_steam_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "好友 SteamID"),
                    app_id = table.Column<int>(type: "INTEGER", nullable: false, comment: "应用 ID"),
                    start_time = table.Column<long>(type: "INTEGER", nullable: false, comment: "第一段重叠的开始（Unix 时间戳，秒）"),
                    end_time = table.Column<long>(type: "INTEGER", nullable: false, comment: "最后一段重叠的结束（Unix 时间戳，秒）"),
                    seconds = table.Column<long>(type: "INTEGER", nullable: false, comment: "在一起的秒数"),
                    confirmed_seconds = table.Column<long>(type: "INTEGER", nullable: false, comment: "其中确认同组的秒数")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_together_record", x => x.id);
                },
                comment: "一局游戏里和一位好友在一起的时间，由会话标签服务算出");

            migrationBuilder.CreateIndex(
                name: "exchange_card_uidx",
                table: "exchange_card",
                columns: new[] { "sender_steam_id", "generated_at" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "friend_achievement_progress_idx",
                table: "friend_achievement_progress",
                columns: new[] { "steam_id", "app_id", "observed_at" });

            migrationBuilder.CreateIndex(
                name: "friend_library_item_first_seen_idx",
                table: "friend_library_item",
                column: "first_seen_at");

            migrationBuilder.CreateIndex(
                name: "friend_library_item_uidx",
                table: "friend_library_item",
                columns: new[] { "steam_id", "app_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "friend_observation_window_account_start_idx",
                table: "friend_observation_window",
                columns: new[] { "account_name", "started_at" });

            migrationBuilder.CreateIndex(
                name: "friend_since_uidx",
                table: "friend_since",
                columns: new[] { "account_name", "friend_steam_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "presence_group_record_observed_at_idx",
                table: "presence_group_record",
                column: "observed_at");

            migrationBuilder.CreateIndex(
                name: "presence_group_record_steam_id_observed_at_idx",
                table: "presence_group_record",
                columns: new[] { "steam_id", "observed_at" });

            migrationBuilder.CreateIndex(
                name: "together_record_friend_start_idx",
                table: "together_record",
                columns: new[] { "friend_steam_id", "start_time" });

            migrationBuilder.CreateIndex(
                name: "together_record_session_idx",
                table: "together_record",
                column: "session_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "exchange_card");

            migrationBuilder.DropTable(
                name: "friend_achievement_progress");

            migrationBuilder.DropTable(
                name: "friend_library_item");

            migrationBuilder.DropTable(
                name: "friend_observation_window");

            migrationBuilder.DropTable(
                name: "friend_since");

            migrationBuilder.DropTable(
                name: "presence_group_record");

            migrationBuilder.DropTable(
                name: "together_record");
        }
    }
}
