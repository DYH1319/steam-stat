using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ElectronNet.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SteamStat.Core.Environment;

namespace ElectronNet.Features.DemoArchive;

/// <summary>生成多大的档案。同样的参数得到完全相同的数据。</summary>
public sealed record FakeArchiveProfile(
    int Seed,
    int Years,
    int Sessions,
    int FriendRecords,
    int Games,
    int Friends,
    DateTimeOffset End)
{
    private static readonly DateTimeOffset FixedEnd = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>演示模式和视觉回归用的小档案。</summary>
    public static FakeArchiveProfile Demo { get; } = new(20261105, 2, 3_000, 8_000, 60, 12, FixedEnd);

    /// <summary>十年压测：10 年、5 万会话、100 万好友记录。</summary>
    public static FakeArchiveProfile Decade { get; } = new(20261106, 10, 50_000, 1_000_000, 400, 60, FixedEnd);
}

public sealed record FakeArchiveResult(int Sessions, int FriendRecords, int Games, int Achievements, TimeSpan Elapsed);

/// <summary>
/// 假档案生成器：演示模式、十年压测和视觉回归共用。行为有规律（晚上玩得多、周末更长、一段时间沉迷一款游戏、
/// 大部分时长集中在少数游戏），否则图表和洞察测不出问题。
/// 游戏、厂商、人名全部是虚构的，应用 ID 从 9,000,000 开始，不会撞上真实的 Steam 应用。
/// </summary>
public static class FakeArchiveGenerator
{
    public const string AccountName = "sample_curator";
    public const string SteamId = "76561198999000001";
    public const string PersonaName = "示例馆长";

    private const uint AccountId = 1038734273;
    private const int FirstAppId = 9_000_000;
    private const string DeviceId = "00000000-0000-4000-8000-000000000001";

    private static readonly string[] Prefixes =
    [
        "Starfall", "Iron", "Hollow", "Ember", "Tidal", "Quiet", "Lantern", "Paper", "Velvet", "Crimson",
        "Northern", "Clockwork", "Sunken", "Glass", "Wandering", "Last", "Silver", "Moss", "Echo", "Amber"
    ];

    private static readonly string[] Suffixes =
    [
        "Harbor", "Frontier", "Tactics", "Odyssey", "Garden", "Protocol", "Keep", "Railway", "Chronicle", "Mines",
        "Orchard", "Station", "Dynasty", "Lighthouse", "Expedition", "Workshop", "Citadel", "Archive", "Skies", "Depths"
    ];

    private static readonly string[] Studios =
    [
        "Lowland Works", "Paper Kite", "North Pier Games", "Tin Lantern", "Second Orbit", "Slow Tide", "Oak & Ember", "Fable Yard"
    ];

    private static readonly string[] Genres = ["Roguelike", "Strategy", "Simulation", "RPG", "Puzzle", "Shooter", "Survival", "Racing"];

    private static readonly string[] Statuses = ["playing", "completed", "paused", "abandoned", "backlog"];

    private static readonly string[] FriendNames =
    [
        "晴川", "Moss", "老周", "Kestrel", "阿森", "Juniper", "Tofu", "小满", "Marlow", "Ibis", "青禾", "Rook"
    ];

    private static readonly string[] PresenceTexts = ["主菜单", "排位赛", "合作模式", "第三章", "地图编辑器", "等待队友", "观战中", "Boss 战"];

    private sealed record Game(int AppId, string Name, string Developer, string Publisher, string Genre, double Weight, int Achievements);

    /// <summary>在 userDataDirectory 下写好数据库和设置，目录必须不存在档案。</summary>
    public static async Task<FakeArchiveResult> GenerateAsync(string userDataDirectory, FakeArchiveProfile profile, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var paths = new AppPaths(userDataDirectory);
        var databaseFile = paths.DatabaseFile;
        if (File.Exists(databaseFile)) throw new InvalidOperationException($"{databaseFile} already exists.");
        Directory.CreateDirectory(paths.DatabaseDirectory);

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(SqliteConnectionStrings.Create(databaseFile, pooling: false)).Options;
        await using (var db = new AppDbContext(options))
            await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

        var random = new Random(profile.Seed);
        var games = CreateGames(random, profile.Games);
        var end = profile.End.ToUnixTimeSeconds();
        var start = profile.End.AddYears(-profile.Years).ToUnixTimeSeconds();

        await using var connection = new SqliteConnection(SqliteConnectionStrings.Create(databaseFile, pooling: false));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await SqliteSchema.ExecuteAsync(connection, "PRAGMA journal_mode = WAL", cancellationToken).ConfigureAwait(false);
        await SqliteSchema.ExecuteAsync(connection, "PRAGMA synchronous = OFF", cancellationToken).ConfigureAwait(false);

        int achievements;
        await using (var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            WriteIdentity(connection, start);
            WriteGames(connection, games, random, start, end);
            var sessions = WriteSessions(connection, games, random, profile, start, end);
            achievements = WriteAchievements(connection, games, sessions, random, end);
            WriteLibrary(connection, games, sessions, random, start, end);
            WriteOfficialPlaytime(connection, games, sessions, random, start, end);
            WriteScreenshots(connection, sessions, random);
            WriteProfileHistory(connection, random, start, end);
            WriteFriends(connection, games, random, profile, start, end);
            WriteRichPresence(connection, sessions, random);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        await SqliteSchema.ExecuteAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE)", cancellationToken).ConfigureAwait(false);

        WriteSettings(paths);
        return new FakeArchiveResult(profile.Sessions, profile.FriendRecords, games.Count, achievements, stopwatch.Elapsed);
    }

    private static void WriteSettings(AppPaths paths)
    {
        Directory.CreateDirectory(paths.SettingsDirectory);
        var file = paths.SettingsFile;
        if (File.Exists(file)) return;
        // 假档案不应该连真实的网络和 Steam：引导跳过、自动更新关闭
        File.WriteAllText(file, JsonSerializer.Serialize(new
        {
            autoUpdate = false,
            onboardingCompleted = true,
            language = "zh-CN",
            analytics = new { rendererMigrated = true }
        }));
    }

    private static List<Game> CreateGames(Random random, int count)
    {
        var games = new List<Game>(count);
        for (var index = 0; index < count; index++)
        {
            var name = $"{Prefixes[index % Prefixes.Length]} {Suffixes[index / Prefixes.Length % Suffixes.Length]}";
            if (index >= Prefixes.Length * Suffixes.Length) name += $" {index / (Prefixes.Length * Suffixes.Length) + 1}";
            var studio = Studios[random.Next(Studios.Length)];
            // 齐夫分布：少数游戏占大部分时长
            games.Add(new Game(FirstAppId + index * 10, name, studio, studio, Genres[random.Next(Genres.Length)], 1.0 / (index + 1), random.Next(0, 80)));
        }
        return games;
    }

    /// <summary>一条预编译的 INSERT，避免一百万次解析语句。</summary>
    private sealed class Inserter : IDisposable
    {
        private readonly SqliteCommand _command;
        private readonly SqliteParameter[] _parameters;

        public Inserter(SqliteConnection connection, string table, params string[] columns)
        {
            _command = connection.CreateCommand();
            _command.CommandText = $"INSERT INTO {table} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", columns.Select((_, index) => $"$p{index}"))})";
            _parameters = columns.Select((_, index) => _command.Parameters.Add(new SqliteParameter($"$p{index}", null))).ToArray();
            _command.Prepare();
        }

        public void Run(params object?[] values)
        {
            for (var index = 0; index < values.Length; index++) _parameters[index].Value = values[index] ?? DBNull.Value;
            _command.ExecuteNonQuery();
        }

        public void Dispose() => _command.Dispose();
    }

    private static void WriteIdentity(SqliteConnection connection, long start)
    {
        using var device = new Inserter(connection, "device", "id", "name", "is_current", "created_at");
        device.Run(DeviceId, "Sample Desktop", 1, start);
        using var user = new Inserter(connection, "steam_user", "steam_id", "account_id", "account_name", "persona_name", "most_recent", "level");
        user.Run(SteamId, AccountId, AccountName, PersonaName, 1, 42);
    }

    private static void WriteGames(SqliteConnection connection, List<Game> games, Random random, long start, long end)
    {
        using var app = new Inserter(connection, "steam_app", "app_id", "name", "installed", "type", "developer", "publisher", "steam_release_date", "is_free_app");
        foreach (var game in games)
            app.Run(game.AppId, game.Name, random.NextDouble() < 0.3 ? 1 : 0, "game", game.Developer, game.Publisher, random.NextInt64(start - 86400L * 365 * 5, end), random.NextDouble() < 0.1 ? 1 : 0);
    }

    private sealed record Session(int AppId, int Start, int Duration);

    private static List<Session> WriteSessions(SqliteConnection connection, List<Game> games, Random random, FakeArchiveProfile profile, long start, long end)
    {
        var totalDays = (int)((end - start) / 86400);
        var weights = new double[totalDays];
        for (var day = 0; day < totalDays; day++)
        {
            var weekday = DateTimeOffset.FromUnixTimeSeconds(start + day * 86400L).DayOfWeek;
            var weekend = weekday is DayOfWeek.Saturday or DayOfWeek.Sunday ? 1.8 : 1.0;
            // 一年里有忙和闲的时候
            var season = 1 + 0.5 * Math.Sin(day / 365.0 * Math.Tau);
            weights[day] = random.NextDouble() < 0.12 ? 0 : weekend * season;
        }
        var scale = profile.Sessions / weights.Sum();

        var sessions = new List<Session>(profile.Sessions);
        var picker = new WeightedPicker(games.Select(game => game.Weight).ToArray());
        var obsessionUntil = 0;
        var obsession = 0;
        for (var day = 0; day < totalDays && sessions.Count < profile.Sessions; day++)
        {
            if (day >= obsessionUntil && random.NextDouble() < 0.04)
            {
                obsession = picker.Next(random);
                obsessionUntil = day + random.Next(5, 40);
            }
            var count = (int)Math.Floor(weights[day] * scale + random.NextDouble());
            long cursor = start + day * 86400L + random.Next(9, 22) * 3600L + random.Next(3600);
            for (var index = 0; index < count && sessions.Count < profile.Sessions; index++)
            {
                var game = day < obsessionUntil && random.NextDouble() < 0.7 ? games[obsession] : games[picker.Next(random)];
                var duration = (int)Math.Clamp(Math.Exp(random.NextDouble() * 2.4 + 6.2), 120, 6 * 3600);
                if (cursor + duration >= end) break;
                sessions.Add(new Session(game.AppId, (int)cursor, duration));
                cursor += duration + random.Next(300, 7200);
            }
        }
        // 总数凑不够时从头补，保证规模参数是准确的
        while (sessions.Count < profile.Sessions)
        {
            var at = random.NextInt64(start, end - 7200);
            sessions.Add(new Session(games[picker.Next(random)].AppId, (int)at, random.Next(300, 7200)));
        }

        using var record = new Inserter(connection, "use_app_record",
            "app_id", "steam_id", "start_time", "end_time", "duration", "last_heartbeat", "idle_duration",
            "foreground_duration", "device_id", "source", "end_reason", "note", "tags");
        foreach (var session in sessions)
        {
            var idle = random.NextDouble() < 0.2 ? (long)(session.Duration * random.NextDouble() * 0.5) : 0;
            var endTime = session.Start + session.Duration;
            var recovered = random.NextDouble() < 0.02;
            record.Run(
                session.AppId, SteamId, session.Start, endTime, session.Duration, endTime, idle,
                (long)(session.Duration * (0.7 + random.NextDouble() * 0.3)), DeviceId, "auto",
                recovered ? "recovered" : "ended",
                random.NextDouble() < 0.01 ? "打到了最后一关" : null,
                random.NextDouble() < 0.01 ? "开黑" : null);
        }
        return sessions;
    }

    private sealed class WeightedPicker
    {
        private readonly double[] _cumulative;

        public WeightedPicker(double[] weights)
        {
            _cumulative = new double[weights.Length];
            var sum = 0.0;
            for (var index = 0; index < weights.Length; index++) _cumulative[index] = sum += weights[index];
        }

        public int Next(Random random)
        {
            var index = Array.BinarySearch(_cumulative, random.NextDouble() * _cumulative[^1]);
            return Math.Min(index < 0 ? ~index : index, _cumulative.Length - 1);
        }
    }

    private static int WriteAchievements(SqliteConnection connection, List<Game> games, List<Session> sessions, Random random, long end)
    {
        var byApp = sessions.GroupBy(session => session.AppId).ToDictionary(group => group.Key, group => group.OrderBy(session => session.Start).ToList());
        using var unlock = new Inserter(connection, "achievement_unlock_archive",
            "account_name", "steam_id", "app_id", "internal_name", "name", "description", "icon", "hidden", "global_percent", "unlock_time", "recorded_at", "group_id", "archived");
        using var game = new Inserter(connection, "achievement_game_archive",
            "account_name", "steam_id", "app_id", "app_name", "total", "unlocked", "progress_total", "progress_unlocked", "all_total", "all_unlocked", "synced_at");
        using var set = new Inserter(connection, "achievement_set_archive",
            "account_name", "steam_id", "app_id", "group_id", "name", "dlc_app_id", "archived", "counted", "owned", "total", "unlocked", "all_total", "completed", "completed_at", "synced_at");
        using var change = new Inserter(connection, "achievement_schema_change",
            "app_id", "schema_hash", "internal_name", "name", "kind", "old_value", "new_value", "detected_at");
        var count = 0;
        foreach (var item in games)
        {
            if (item.Achievements == 0 || !byApp.TryGetValue(item.AppId, out var played)) continue;
            var hours = played.Sum(session => session.Duration) / 3600.0;
            var unlocked = Math.Min(item.Achievements, (int)(hours * (0.4 + random.NextDouble())));
            long lastUnlock = 0;
            for (var index = 0; index < unlocked; index++)
            {
                var session = played[Math.Min(played.Count - 1, (int)((double)index / unlocked * played.Count))];
                var time = session.Start + random.Next(Math.Max(1, session.Duration));
                lastUnlock = Math.Max(lastUnlock, time);
                // 少量绝版成就：解锁后被开发者归档
                var archived = index % 37 == 36 ? 1 : 0;
                if (archived == 1)
                    change.Run(item.AppId, item.AppId * 1000 + index, $"ACH_{index:D3}", $"里程碑 {index + 1}", "archived", null, null, end);
                unlock.Run(AccountName, SteamId, item.AppId, $"ACH_{index:D3}", $"里程碑 {index + 1}", $"在《{item.Name}》里达成第 {index + 1} 个目标",
                    string.Empty, random.NextDouble() < 0.1 ? 1 : 0, Math.Round(random.NextDouble() * 80 + 0.2, 1), time, end, 0, archived);
                count++;
            }
            // 每款第四款游戏带一个未开始的 DLC 集：拥有与否交替
            var dlc = item.Achievements >= 20 && item.AppId % 4 == 0 ? item.Achievements / 4 : 0;
            var dlcOwned = item.AppId % 8 == 0;
            var baseTotal = Math.Max(item.Achievements - dlc, unlocked);
            var counted = baseTotal + (dlcOwned ? dlc : 0);
            var completed = baseTotal > 0 && unlocked >= baseTotal;
            set.Run(AccountName, SteamId, item.AppId, 0, string.Empty, null, 0, 1, null, baseTotal, unlocked, baseTotal,
                completed ? 1 : 0, completed ? lastUnlock : null, end);
            if (dlc > 0)
                set.Run(AccountName, SteamId, item.AppId, 1, "扩展内容", item.AppId + 1, 0, dlcOwned ? 1 : 0, dlcOwned ? 1 : 0, dlc, 0, dlc, 0, null, end);
            game.Run(AccountName, SteamId, item.AppId, item.Name, counted, unlocked, counted, unlocked, baseTotal + dlc, unlocked, end);
        }
        return count;
    }

    private static void WriteLibrary(SqliteConnection connection, List<Game> games, List<Session> sessions, Random random, long start, long end)
    {
        var firstPlayed = sessions.GroupBy(session => session.AppId).ToDictionary(group => group.Key, group => group.Min(session => session.Start));
        using var acquisition = new Inserter(connection, "library_acquisition", "account_name", "app_id", "package_id", "acquired_at");
        using var meta = new Inserter(connection, "library_game_meta", "account_name", "app_id", "status", "completed_at", "rating", "updated_at");
        foreach (var game in games)
        {
            var before = firstPlayed.TryGetValue(game.AppId, out var played) ? played : end;
            acquisition.Run(AccountName, game.AppId, game.AppId / 10, random.NextInt64(start, Math.Max(start + 1, before)));
            if (random.NextDouble() >= 0.4) continue;
            var status = Statuses[random.Next(Statuses.Length)];
            meta.Run(AccountName, game.AppId, status, status == "completed" ? random.NextInt64(start, end) : null, random.Next(1, 6), end);
        }
    }

    private static void WriteOfficialPlaytime(SqliteConnection connection, List<Game> games, List<Session> sessions, Random random, long start, long end)
    {
        using var snapshot = new Inserter(connection, "playtime_snapshot",
            "steam_id", "app_id", "playtime_minutes", "source", "first_observed_at", "last_observed_at", "last_played_at");
        foreach (var group in sessions.GroupBy(session => session.AppId))
        {
            var ordered = group.OrderBy(session => session.Start + session.Duration).ToList();
            var history = random.Next(0, 3000);
            snapshot.Run(SteamId, group.Key, history, "cm", start, start, null);
            var minutes = history;
            var step = Math.Max(1, ordered.Count / 6);
            for (var index = step - 1; index < ordered.Count; index += step)
            {
                minutes += ordered.Skip(Math.Max(0, index - step + 1)).Take(step).Sum(session => session.Duration) / 60;
                // 少数区间官方时长比本地多：在别的设备上玩过，体检要能发现
                if (random.NextDouble() < 0.05) minutes += random.Next(60, 300);
                var observed = ordered[index].Start + ordered[index].Duration + 600;
                snapshot.Run(SteamId, group.Key, minutes, "cm", observed, observed, ordered[index].Start);
            }
        }
    }

    private static void WriteScreenshots(SqliteConnection connection, List<Session> sessions, Random random)
    {
        using var screenshot = new Inserter(connection, "screenshot_archive",
            "account_id", "steam_id", "app_id", "file_name", "file_path", "taken_at", "file_size", "width", "height", "indexed_at");
        foreach (var session in sessions.Where(_ => random.NextDouble() < 0.05))
        {
            var taken = session.Start + random.Next(session.Duration);
            var name = $"{taken}_1.jpg";
            screenshot.Run(AccountId, SteamId, session.AppId, name, $"/fake/760/remote/{session.AppId}/screenshots/{name}", taken, 512_000, 1920, 1080, taken);
        }
    }

    private static void WriteProfileHistory(SqliteConnection connection, Random random, long start, long end)
    {
        using var history = new Inserter(connection, "profile_history", "steam_id", "field", "value", "previous_value", "observed_at", "source");
        var level = 10;
        for (var at = start; at < end; at += random.Next(30, 120) * 86400L)
        {
            history.Run(SteamId, "level", (level + 1).ToString(CultureInfo.InvariantCulture), level.ToString(CultureInfo.InvariantCulture), at, "cm");
            level++;
        }
    }

    private static void WriteFriends(SqliteConnection connection, List<Game> games, Random random, FakeArchiveProfile profile, long start, long end)
    {
        using var tracking = new Inserter(connection, "friend_tracking", "account_name", "friend_steam_id", "created_at");
        using var record = new Inserter(connection, "friend_status_record",
            "account_name", "friend_steam_id", "friend_persona_name", "change_type", "previous_value", "current_value", "timestamp");
        var friends = Enumerable.Range(0, profile.Friends)
            .Select(index => (Id: (76561198999100000L + index).ToString(CultureInfo.InvariantCulture), Name: FriendNames[index % FriendNames.Length] + (index < FriendNames.Length ? string.Empty : $" {index}")))
            .ToList();
        foreach (var friend in friends) tracking.Run(AccountName, friend.Id, start);

        var span = end - start;
        var perFriend = profile.FriendRecords / Math.Max(1, friends.Count);
        foreach (var friend in friends)
        {
            var state = 1;
            var game = 0;
            var text = string.Empty;
            for (var index = 0; index < perFriend; index++)
            {
                var at = start + span * index / perFriend;
                var roll = random.NextDouble();
                if (roll < 0.5)
                {
                    var next = state == 1 ? 3 : 1;
                    record.Run(AccountName, friend.Id, friend.Name, "state", $"{{\"personaState\":{state}}}", $"{{\"personaState\":{next}}}", at);
                    state = next;
                }
                else if (roll < 0.85)
                {
                    var next = game == 0 ? games[random.Next(Math.Min(games.Count, 40))] : null;
                    var nextId = next?.AppId ?? 0;
                    record.Run(AccountName, friend.Id, friend.Name, "game",
                        $"{{\"gameId\":\"{game}\",\"gameName\":\"\"}}",
                        $"{{\"gameId\":\"{nextId}\",\"gameName\":{JsonSerializer.Serialize(next?.Name ?? string.Empty)}}}", at);
                    game = nextId;
                }
                else
                {
                    var next = PresenceTexts[random.Next(PresenceTexts.Length)];
                    record.Run(AccountName, friend.Id, friend.Name, "richPresence",
                        $"{{\"richPresence\":{JsonSerializer.Serialize(text)}}}", $"{{\"richPresence\":{JsonSerializer.Serialize(next)}}}", at);
                    text = next;
                }
            }
        }
    }

    private static void WriteRichPresence(SqliteConnection connection, List<Session> sessions, Random random)
    {
        using var record = new Inserter(connection, "rich_presence_record",
            "account_name", "steam_id", "is_self", "app_id", "raw_values", "display_text", "source", "observed_at");
        foreach (var session in sessions.Where(_ => random.NextDouble() < 0.2))
        {
            var text = PresenceTexts[random.Next(PresenceTexts.Length)];
            record.Run(AccountName, SteamId, 1, session.AppId, JsonSerializer.Serialize(new Dictionary<string, string> { ["status"] = text }), text, "push", session.Start + 60);
        }
    }
}
