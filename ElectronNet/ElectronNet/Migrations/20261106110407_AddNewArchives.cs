using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElectronNet.Migrations
{
    /// <inheritdoc />
    public partial class AddNewArchives : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "palette",
                table: "screenshot_archive",
                type: "TEXT",
                maxLength: 64,
                nullable: true,
                comment: "主色（逗号分隔的十六进制颜色）");

            migrationBuilder.AddColumn<string>(
                name: "category_ids",
                table: "app_store_status",
                type: "TEXT",
                nullable: true,
                comment: "商店分类 ID（JSON）");

            migrationBuilder.AddColumn<string>(
                name: "tag_ids",
                table: "app_store_status",
                type: "TEXT",
                nullable: true,
                comment: "商店标签 ID（JSON）");

            migrationBuilder.AddColumn<double>(
                name: "first_global_percent",
                table: "achievement_unlock_archive",
                type: "REAL",
                nullable: true,
                comment: "第一次观测到的全球解锁率");

            migrationBuilder.AddColumn<long>(
                name: "first_percent_at",
                table: "achievement_unlock_archive",
                type: "INTEGER",
                nullable: true,
                comment: "第一次观测的时间（Unix 时间戳，秒）");

            migrationBuilder.CreateTable(
                name: "app_artifact_event",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    app_id = table.Column<int>(type: "INTEGER", nullable: false, comment: "应用 ID"),
                    kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "事件类型"),
                    observed_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "发现时间（Unix 时间戳，秒）"),
                    before_value = table.Column<string>(type: "TEXT", nullable: true, comment: "变化前"),
                    after_value = table.Column<string>(type: "TEXT", nullable: true, comment: "变化后")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_artifact_event", x => x.id);
                },
                comment: "数字文物：下架、改名、换封面、疑似关服");

            migrationBuilder.CreateTable(
                name: "app_install_event",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    app_id = table.Column<int>(type: "INTEGER", nullable: false, comment: "应用 ID"),
                    kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, comment: "baseline / installed / uninstalled"),
                    observed_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "观测时间（Unix 时间戳，秒）"),
                    size_on_disk = table.Column<long>(type: "INTEGER", nullable: true, comment: "占用空间（字节）"),
                    build_id = table.Column<int>(type: "INTEGER", nullable: true, comment: "版本号")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_install_event", x => x.id);
                },
                comment: "本机观测到的安装和卸载");

            migrationBuilder.CreateTable(
                name: "app_player_count",
                columns: table => new
                {
                    app_id = table.Column<int>(type: "INTEGER", nullable: false, comment: "应用 ID"),
                    day = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false, comment: "UTC 日期"),
                    peak = table.Column<int>(type: "INTEGER", nullable: false, comment: "当天采样的最大值"),
                    low = table.Column<int>(type: "INTEGER", nullable: false, comment: "当天采样的最小值"),
                    samples = table.Column<int>(type: "INTEGER", nullable: false, comment: "采样次数，0 表示没有人数数据")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_player_count", x => new { x.app_id, x.day });
                },
                comment: "全球在线人数的每日采样");

            migrationBuilder.CreateTable(
                name: "collector_state",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "采集器 ID"),
                    last_run_at = table.Column<long>(type: "INTEGER", nullable: true, comment: "上次运行时间（Unix 时间戳，秒）"),
                    last_success_at = table.Column<long>(type: "INTEGER", nullable: true, comment: "上次成功时间（Unix 时间戳，秒）"),
                    last_error = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true, comment: "上次失败的原因"),
                    last_items = table.Column<int>(type: "INTEGER", nullable: true, comment: "上次采集到的条目数"),
                    data = table.Column<string>(type: "TEXT", nullable: true, comment: "采集器自己的状态（JSON）")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_collector_state", x => x.id);
                },
                comment: "档案采集器的运行状态");

            migrationBuilder.CreateTable(
                name: "community_comment",
                columns: table => new
                {
                    steam_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "资料页所属账号"),
                    comment_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "留言 ID"),
                    author_steam_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true, comment: "留言人 SteamID"),
                    author_name = table.Column<string>(type: "TEXT", nullable: false, comment: "留言人昵称"),
                    posted_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "留言时间（Unix 时间戳，秒）"),
                    text = table.Column<string>(type: "TEXT", nullable: false, comment: "内容"),
                    first_seen_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "第一次看到的时间"),
                    last_seen_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "最后看到的时间"),
                    missing_since = table.Column<long>(type: "INTEGER", nullable: true, comment: "从页面上消失的时间")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_community_comment", x => new { x.steam_id, x.comment_id });
                },
                comment: "资料页收到的留言");

            migrationBuilder.CreateTable(
                name: "community_file",
                columns: table => new
                {
                    steam_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "账号 SteamID"),
                    kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, comment: "guides / favorites"),
                    published_file_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "文件 ID"),
                    app_id = table.Column<int>(type: "INTEGER", nullable: false, comment: "应用 ID"),
                    title = table.Column<string>(type: "TEXT", nullable: false, comment: "标题"),
                    description = table.Column<string>(type: "TEXT", nullable: true, comment: "简介"),
                    author = table.Column<string>(type: "TEXT", nullable: true, comment: "作者"),
                    preview_url = table.Column<string>(type: "TEXT", nullable: true, comment: "预览图"),
                    first_seen_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "第一次看到的时间"),
                    last_seen_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "最后看到的时间"),
                    missing_since = table.Column<long>(type: "INTEGER", nullable: true, comment: "从页面上消失的时间")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_community_file", x => new { x.steam_id, x.kind, x.published_file_id });
                },
                comment: "写过的指南和收藏的工坊物品");

            migrationBuilder.CreateTable(
                name: "community_review",
                columns: table => new
                {
                    steam_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "账号 SteamID"),
                    review_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "评测 ID"),
                    app_id = table.Column<int>(type: "INTEGER", nullable: false, comment: "应用 ID"),
                    recommended = table.Column<bool>(type: "INTEGER", nullable: false, comment: "推荐"),
                    hours_total = table.Column<double>(type: "REAL", nullable: true, comment: "总时长（小时）"),
                    hours_at_review = table.Column<double>(type: "REAL", nullable: true, comment: "写评测时的时长（小时）"),
                    posted_at = table.Column<long>(type: "INTEGER", nullable: true, comment: "发布时间（Unix 时间戳，秒）"),
                    edited_at = table.Column<long>(type: "INTEGER", nullable: true, comment: "最后编辑时间"),
                    text = table.Column<string>(type: "TEXT", nullable: false, comment: "内容"),
                    helpful = table.Column<int>(type: "INTEGER", nullable: false, comment: "觉得有用的人数"),
                    funny = table.Column<int>(type: "INTEGER", nullable: false, comment: "觉得欢乐的人数"),
                    first_seen_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "第一次看到的时间"),
                    last_seen_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "最后看到的时间"),
                    missing_since = table.Column<long>(type: "INTEGER", nullable: true, comment: "从页面上消失的时间")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_community_review", x => new { x.steam_id, x.review_id });
                },
                comment: "写过的评测");

            migrationBuilder.CreateTable(
                name: "family_member",
                columns: table => new
                {
                    family_group_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "家庭组 ID"),
                    steam_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "成员 SteamID"),
                    name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true, comment: "昵称"),
                    role = table.Column<int>(type: "INTEGER", nullable: false, comment: "角色"),
                    joined_at = table.Column<long>(type: "INTEGER", nullable: true, comment: "加入时间（Unix 时间戳，秒）"),
                    former = table.Column<bool>(type: "INTEGER", nullable: false, comment: "已退出"),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "更新时间")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_family_member", x => new { x.family_group_id, x.steam_id });
                },
                comment: "家庭组成员");

            migrationBuilder.CreateTable(
                name: "family_playtime",
                columns: table => new
                {
                    family_group_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "家庭组 ID"),
                    steam_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "成员 SteamID"),
                    app_id = table.Column<int>(type: "INTEGER", nullable: false, comment: "应用 ID"),
                    by_owner = table.Column<bool>(type: "INTEGER", nullable: false, comment: "来自 entries_by_owner"),
                    first_played = table.Column<long>(type: "INTEGER", nullable: true, comment: "首玩时间（Unix 时间戳，秒）"),
                    latest_played = table.Column<long>(type: "INTEGER", nullable: true, comment: "最后游玩时间（Unix 时间戳，秒）"),
                    seconds = table.Column<long>(type: "INTEGER", nullable: false, comment: "累计秒数"),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "更新时间")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_family_playtime", x => new { x.family_group_id, x.steam_id, x.app_id, x.by_owner });
                },
                comment: "家庭共享时长");

            migrationBuilder.CreateTable(
                name: "friend_playtime_snapshot",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    steam_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "好友 SteamID"),
                    app_id = table.Column<int>(type: "INTEGER", nullable: false, comment: "应用 ID"),
                    playtime_minutes = table.Column<int>(type: "INTEGER", nullable: false, comment: "累计时长（分钟）"),
                    observed_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "观测时间（Unix 时间戳，秒）")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_friend_playtime_snapshot", x => x.id);
                },
                comment: "手动追踪的好友的公开时长，值变化才写");

            migrationBuilder.CreateTable(
                name: "platform_playtime",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    steam_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "账号 SteamID"),
                    app_id = table.Column<int>(type: "INTEGER", nullable: false, comment: "应用 ID"),
                    observed_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "观测时间（Unix 时间戳，秒）"),
                    windows_minutes = table.Column<int>(type: "INTEGER", nullable: false, comment: "Windows 时长（分钟）"),
                    mac_minutes = table.Column<int>(type: "INTEGER", nullable: false, comment: "macOS 时长（分钟）"),
                    linux_minutes = table.Column<int>(type: "INTEGER", nullable: false, comment: "Linux 时长（分钟）"),
                    deck_minutes = table.Column<int>(type: "INTEGER", nullable: false, comment: "Steam Deck 时长（分钟）"),
                    disconnected_minutes = table.Column<int>(type: "INTEGER", nullable: false, comment: "离线时长（分钟）"),
                    first_played = table.Column<long>(type: "INTEGER", nullable: true, comment: "官方首玩时间（Unix 时间戳，秒）"),
                    last_played = table.Column<long>(type: "INTEGER", nullable: true, comment: "官方最后游玩时间（Unix 时间戳，秒）"),
                    first_windows = table.Column<long>(type: "INTEGER", nullable: true, comment: "Windows 首玩时间"),
                    first_mac = table.Column<long>(type: "INTEGER", nullable: true, comment: "macOS 首玩时间"),
                    first_linux = table.Column<long>(type: "INTEGER", nullable: true, comment: "Linux 首玩时间"),
                    first_deck = table.Column<long>(type: "INTEGER", nullable: true, comment: "Steam Deck 首玩时间"),
                    last_windows = table.Column<long>(type: "INTEGER", nullable: true, comment: "Windows 最后游玩时间"),
                    last_mac = table.Column<long>(type: "INTEGER", nullable: true, comment: "macOS 最后游玩时间"),
                    last_linux = table.Column<long>(type: "INTEGER", nullable: true, comment: "Linux 最后游玩时间"),
                    last_deck = table.Column<long>(type: "INTEGER", nullable: true, comment: "Steam Deck 最后游玩时间")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_playtime", x => x.id);
                },
                comment: "Steam 按平台记录的时长，值变化才写");

            migrationBuilder.CreateTable(
                name: "purchase_record",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    steam_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "账号 SteamID"),
                    row_key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, comment: "交易号或内容哈希"),
                    date = table.Column<long>(type: "INTEGER", nullable: false, comment: "日期（Unix 时间戳，秒，当天 0 点）"),
                    kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, comment: "purchase / gift / ingame / market / wallet / refund / other"),
                    type_text = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, comment: "账单上的类型文字"),
                    total_cents = table.Column<long>(type: "INTEGER", nullable: false, comment: "金额 × 100"),
                    currency = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true, comment: "币种"),
                    wallet_change_cents = table.Column<long>(type: "INTEGER", nullable: true, comment: "钱包变化 × 100"),
                    payment_method = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true, comment: "付款方式"),
                    refunded = table.Column<bool>(type: "INTEGER", nullable: false, comment: "已退款"),
                    items = table.Column<string>(type: "TEXT", nullable: false, comment: "商品和匹配到的应用（JSON）"),
                    first_seen_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "第一次看到的时间")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_purchase_record", x => x.id);
                },
                comment: "Steam 账单");

            migrationBuilder.CreateTable(
                name: "recording_media",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    account_id = table.Column<long>(type: "INTEGER", nullable: false, comment: "32 位账号 ID"),
                    steam_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "账号 SteamID"),
                    media_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, comment: "文件夹名"),
                    kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, comment: "background / clip"),
                    app_id = table.Column<int>(type: "INTEGER", nullable: false, comment: "应用 ID"),
                    started_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "开始时间（Unix 时间戳，秒）"),
                    duration_ms = table.Column<long>(type: "INTEGER", nullable: false, comment: "时长（毫秒）"),
                    size_bytes = table.Column<long>(type: "INTEGER", nullable: false, comment: "大小（字节）"),
                    name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true, comment: "片段名称"),
                    timeline_ids = table.Column<string>(type: "TEXT", nullable: true, comment: "关联的时间线（JSON）"),
                    folder_path = table.Column<string>(type: "TEXT", nullable: false, comment: "文件夹路径"),
                    thumbnail_path = table.Column<string>(type: "TEXT", nullable: true, comment: "缩略图路径"),
                    markers = table.Column<string>(type: "TEXT", nullable: true, comment: "片段标记（JSON）"),
                    indexed_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "索引时间"),
                    missing_since = table.Column<long>(type: "INTEGER", nullable: true, comment: "文件消失的时间")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recording_media", x => x.id);
                },
                comment: "磁盘上的录像和片段");

            migrationBuilder.CreateTable(
                name: "recording_timeline",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    account_id = table.Column<long>(type: "INTEGER", nullable: false, comment: "32 位账号 ID"),
                    steam_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "账号 SteamID"),
                    timeline_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, comment: "时间线 ID"),
                    app_id = table.Column<int>(type: "INTEGER", nullable: false, comment: "应用 ID"),
                    started_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "开始时间（Unix 时间戳，秒）"),
                    duration_ms = table.Column<long>(type: "INTEGER", nullable: false, comment: "时长（毫秒）"),
                    markers = table.Column<string>(type: "TEXT", nullable: false, comment: "时间线标记（JSON）"),
                    indexed_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "索引时间")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recording_timeline", x => x.id);
                },
                comment: "Steam 录像时间线");

            migrationBuilder.CreateTable(
                name: "store_page_snapshot",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    app_id = table.Column<int>(type: "INTEGER", nullable: false, comment: "应用 ID"),
                    captured_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "快照时间（Unix 时间戳，秒）"),
                    fingerprint = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "内容指纹"),
                    facts = table.Column<string>(type: "TEXT", nullable: false, comment: "商店页内容（JSON）"),
                    cover_file = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true, comment: "本地封面文件名")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_store_page_snapshot", x => x.id);
                },
                comment: "商店页快照，内容变化时写入");

            migrationBuilder.CreateTable(
                name: "web_capture",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    steam_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "账号 SteamID"),
                    kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, comment: "数据种类"),
                    page = table.Column<int>(type: "INTEGER", nullable: false, comment: "页码"),
                    captured_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "抓取时间（Unix 时间戳，秒）"),
                    file = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, comment: "文件相对路径"),
                    sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, comment: "内容哈希"),
                    size = table.Column<long>(type: "INTEGER", nullable: false, comment: "压缩后字节数"),
                    parser_version = table.Column<int>(type: "INTEGER", nullable: false, comment: "解析器版本"),
                    state = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, comment: "ok / layout"),
                    items = table.Column<int>(type: "INTEGER", nullable: false, comment: "解析出的条目数")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_web_capture", x => x.id);
                },
                comment: "保存下来的原始网页");

            migrationBuilder.CreateTable(
                name: "wishlist_record",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false, comment: "ID")
                        .Annotation("Sqlite:Autoincrement", true),
                    steam_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "账号 SteamID"),
                    app_id = table.Column<int>(type: "INTEGER", nullable: false, comment: "应用 ID"),
                    added_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "加入时间（Unix 时间戳，秒）"),
                    first_seen_at = table.Column<long>(type: "INTEGER", nullable: false, comment: "第一次看到的时间"),
                    removed_at = table.Column<long>(type: "INTEGER", nullable: true, comment: "移除时间"),
                    priority = table.Column<int>(type: "INTEGER", nullable: true, comment: "排序")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wishlist_record", x => x.id);
                },
                comment: "愿望单历史：加入到移除的每一段");

            migrationBuilder.CreateIndex(
                name: "app_artifact_event_app_idx",
                table: "app_artifact_event",
                columns: new[] { "app_id", "observed_at" });

            migrationBuilder.CreateIndex(
                name: "app_install_event_app_idx",
                table: "app_install_event",
                columns: new[] { "app_id", "observed_at" });

            migrationBuilder.CreateIndex(
                name: "community_comment_time_idx",
                table: "community_comment",
                columns: new[] { "steam_id", "posted_at" });

            migrationBuilder.CreateIndex(
                name: "friend_playtime_snapshot_idx",
                table: "friend_playtime_snapshot",
                columns: new[] { "steam_id", "app_id", "observed_at" });

            migrationBuilder.CreateIndex(
                name: "platform_playtime_app_idx",
                table: "platform_playtime",
                columns: new[] { "steam_id", "app_id", "observed_at" });

            migrationBuilder.CreateIndex(
                name: "purchase_record_date_idx",
                table: "purchase_record",
                column: "date");

            migrationBuilder.CreateIndex(
                name: "purchase_record_uidx",
                table: "purchase_record",
                columns: new[] { "steam_id", "row_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "recording_media_app_idx",
                table: "recording_media",
                columns: new[] { "app_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "recording_media_uidx",
                table: "recording_media",
                columns: new[] { "account_id", "media_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "recording_timeline_app_idx",
                table: "recording_timeline",
                columns: new[] { "app_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "recording_timeline_uidx",
                table: "recording_timeline",
                columns: new[] { "account_id", "timeline_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "store_page_snapshot_app_idx",
                table: "store_page_snapshot",
                columns: new[] { "app_id", "captured_at" });

            migrationBuilder.CreateIndex(
                name: "web_capture_kind_idx",
                table: "web_capture",
                columns: new[] { "steam_id", "kind", "captured_at" });

            migrationBuilder.CreateIndex(
                name: "wishlist_record_uidx",
                table: "wishlist_record",
                columns: new[] { "steam_id", "app_id", "added_at" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_artifact_event");

            migrationBuilder.DropTable(
                name: "app_install_event");

            migrationBuilder.DropTable(
                name: "app_player_count");

            migrationBuilder.DropTable(
                name: "collector_state");

            migrationBuilder.DropTable(
                name: "community_comment");

            migrationBuilder.DropTable(
                name: "community_file");

            migrationBuilder.DropTable(
                name: "community_review");

            migrationBuilder.DropTable(
                name: "family_member");

            migrationBuilder.DropTable(
                name: "family_playtime");

            migrationBuilder.DropTable(
                name: "friend_playtime_snapshot");

            migrationBuilder.DropTable(
                name: "platform_playtime");

            migrationBuilder.DropTable(
                name: "purchase_record");

            migrationBuilder.DropTable(
                name: "recording_media");

            migrationBuilder.DropTable(
                name: "recording_timeline");

            migrationBuilder.DropTable(
                name: "store_page_snapshot");

            migrationBuilder.DropTable(
                name: "web_capture");

            migrationBuilder.DropTable(
                name: "wishlist_record");

            migrationBuilder.DropColumn(
                name: "palette",
                table: "screenshot_archive");

            migrationBuilder.DropColumn(
                name: "category_ids",
                table: "app_store_status");

            migrationBuilder.DropColumn(
                name: "tag_ids",
                table: "app_store_status");

            migrationBuilder.DropColumn(
                name: "first_global_percent",
                table: "achievement_unlock_archive");

            migrationBuilder.DropColumn(
                name: "first_percent_at",
                table: "achievement_unlock_archive");
        }
    }
}
