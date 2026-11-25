using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElectronNet.Migrations
{
    /// <inheritdoc />
    public partial class AddGamePlanner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "game_journal",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    sync_uid = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true, comment: "多设备同步 ID"),
                    app_id = table.Column<long>(type: "INTEGER", nullable: false, comment: "应用 ID"),
                    kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, comment: "类型（note/shelve/milestone/review/resume）"),
                    body = table.Column<string>(type: "TEXT", maxLength: 20000, nullable: false, comment: "正文（Markdown）"),
                    spoiler = table.Column<bool>(type: "INTEGER", nullable: false, comment: "含剧透"),
                    mood = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true, comment: "心情"),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "写下的时间（Unix 时间戳，秒）"),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "修改时间（Unix 时间戳，秒）")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_game_journal", x => x.id);
                },
                comment: "游戏心得");

            migrationBuilder.CreateTable(
                name: "game_journey",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    app_id = table.Column<long>(type: "INTEGER", nullable: false, comment: "应用 ID"),
                    started_at = table.Column<long>(type: "INTEGER", nullable: true, comment: "开坑时间（Unix 时间戳，秒）"),
                    goal_kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true, comment: "完成预期（story/storyPlus/achievements/playtime/custom/none）"),
                    goal_hours = table.Column<int>(type: "INTEGER", nullable: true, comment: "时长目标（小时）"),
                    goal_achievement_percent = table.Column<int>(type: "INTEGER", nullable: true, comment: "成就目标（%）"),
                    goal_note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true, comment: "完成预期说明"),
                    milestones = table.Column<string>(type: "TEXT", maxLength: 20000, nullable: true, comment: "里程碑清单（JSON）"),
                    completion_level = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true, comment: "完成程度（beaten/goalReached/perfect）"),
                    shelved_reason = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true, comment: "搁置原因"),
                    resume_at = table.Column<long>(type: "INTEGER", nullable: true, comment: "回坑提醒时间（Unix 时间戳，秒）"),
                    resume_note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true, comment: "回坑时要做什么"),
                    queue_order = table.Column<double>(type: "REAL", nullable: true, comment: "「下一个」队列顺序"),
                    review_scores = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true, comment: "分项评分（JSON）"),
                    review_summary = table.Column<string>(type: "TEXT", maxLength: 280, nullable: true, comment: "一句话评价"),
                    recommend = table.Column<bool>(type: "INTEGER", nullable: true, comment: "是否推荐"),
                    would_replay = table.Column<bool>(type: "INTEGER", nullable: true, comment: "是否会重玩"),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "修改时间（Unix 时间戳，秒）")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_game_journey", x => x.id);
                },
                comment: "游戏规划：开坑、完成预期、搁置、队列与完成评价（每个应用一行，不分账号）");

            migrationBuilder.CreateTable(
                name: "game_playthrough",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    sync_uid = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true, comment: "多设备同步 ID"),
                    app_id = table.Column<long>(type: "INTEGER", nullable: false, comment: "应用 ID"),
                    label = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true, comment: "名称（一周目、DLC……）"),
                    started_at = table.Column<long>(type: "INTEGER", nullable: true, comment: "开始时间（Unix 时间戳，秒）"),
                    ended_at = table.Column<long>(type: "INTEGER", nullable: true, comment: "结束时间（Unix 时间戳，秒）"),
                    outcome = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, comment: "结果（ongoing/completed/abandoned）"),
                    completion_level = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true, comment: "完成程度"),
                    playtime_minutes = table.Column<int>(type: "INTEGER", nullable: true, comment: "用时（分钟）"),
                    rating = table.Column<int>(type: "INTEGER", nullable: true, comment: "这一周目的评分（1~10）"),
                    note = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true, comment: "备注"),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "创建时间（Unix 时间戳，秒）"),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "修改时间（Unix 时间戳，秒）")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_game_playthrough", x => x.id);
                },
                comment: "游戏周目");

            migrationBuilder.CreateTable(
                name: "game_tag",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    sync_uid = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true, comment: "多设备同步 ID"),
                    name = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false, comment: "名称"),
                    color = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, comment: "颜色"),
                    icon = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true, comment: "图标"),
                    sort_order = table.Column<int>(type: "INTEGER", nullable: false, comment: "排序"),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "创建时间（Unix 时间戳，秒）"),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "修改时间（Unix 时间戳，秒）")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_game_tag", x => x.id);
                },
                comment: "自定义游戏分类标签");

            migrationBuilder.CreateTable(
                name: "game_tag_link",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    tag_uid = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "标签的同步 ID"),
                    app_id = table.Column<long>(type: "INTEGER", nullable: false, comment: "应用 ID"),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "创建时间（Unix 时间戳，秒）")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_game_tag_link", x => x.id);
                },
                comment: "标签与游戏的关联");

            migrationBuilder.CreateTable(
                name: "play_challenge",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    sync_uid = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true, comment: "多设备同步 ID"),
                    year = table.Column<int>(type: "INTEGER", nullable: false, comment: "年份"),
                    kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, comment: "类型"),
                    target = table.Column<int>(type: "INTEGER", nullable: false, comment: "目标数量"),
                    title = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true, comment: "标题"),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "创建时间（Unix 时间戳，秒）"),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "修改时间（Unix 时间戳，秒）")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_play_challenge", x => x.id);
                },
                comment: "年度挑战");

            migrationBuilder.CreateTable(
                name: "play_plan",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    sync_uid = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true, comment: "多设备同步 ID"),
                    app_id = table.Column<long>(type: "INTEGER", nullable: true, comment: "应用 ID"),
                    title = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true, comment: "标题"),
                    kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, comment: "类型（day/range/bucket）"),
                    start_day = table.Column<int>(type: "INTEGER", nullable: false, comment: "开始的逻辑日（距 1970-01-01 的天数）"),
                    end_day = table.Column<int>(type: "INTEGER", nullable: true, comment: "结束的逻辑日，单日计划为空"),
                    bucket = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true, comment: "粗略时段（week/month/quarter/year）"),
                    estimated_minutes = table.Column<int>(type: "INTEGER", nullable: true, comment: "预计时长（分钟）"),
                    status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, comment: "状态（pending/done/skipped）"),
                    note = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true, comment: "备注"),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "创建时间（Unix 时间戳，秒）"),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "修改时间（Unix 时间戳，秒）")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_play_plan", x => x.id);
                },
                comment: "游玩计划");

            migrationBuilder.CreateIndex(
                name: "game_journal_app_idx",
                table: "game_journal",
                columns: new[] { "app_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "game_journal_sync_uid_uidx",
                table: "game_journal",
                column: "sync_uid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "game_journey_app_uidx",
                table: "game_journey",
                column: "app_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "game_playthrough_app_idx",
                table: "game_playthrough",
                column: "app_id");

            migrationBuilder.CreateIndex(
                name: "game_playthrough_sync_uid_uidx",
                table: "game_playthrough",
                column: "sync_uid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "game_tag_sync_uid_uidx",
                table: "game_tag",
                column: "sync_uid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "game_tag_link_app_idx",
                table: "game_tag_link",
                column: "app_id");

            migrationBuilder.CreateIndex(
                name: "game_tag_link_uidx",
                table: "game_tag_link",
                columns: new[] { "tag_uid", "app_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "play_challenge_sync_uid_uidx",
                table: "play_challenge",
                column: "sync_uid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "play_challenge_year_idx",
                table: "play_challenge",
                column: "year");

            migrationBuilder.CreateIndex(
                name: "play_plan_start_idx",
                table: "play_plan",
                column: "start_day");

            migrationBuilder.CreateIndex(
                name: "play_plan_sync_uid_uidx",
                table: "play_plan",
                column: "sync_uid",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "game_journal");

            migrationBuilder.DropTable(
                name: "game_journey");

            migrationBuilder.DropTable(
                name: "game_playthrough");

            migrationBuilder.DropTable(
                name: "game_tag");

            migrationBuilder.DropTable(
                name: "game_tag_link");

            migrationBuilder.DropTable(
                name: "play_challenge");

            migrationBuilder.DropTable(
                name: "play_plan");
        }
    }
}
