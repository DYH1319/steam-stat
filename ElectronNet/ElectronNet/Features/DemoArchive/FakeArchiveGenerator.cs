using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ElectronNet.Features.SteamCache.Persistence;
using ElectronNet.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Core.Environment;
using SteamStat.Core.Features.Library;
using SteamStat.Core.Steam.Cache;
using SteamStat.Core.Steam.Gateway;

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

    private static readonly string[] Statuses = ["playing", "completed", "shelved", "abandoned", "wantToPlay"];

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
        List<Session> allSessions;
        List<Game> backlog;
        await using (var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            WriteIdentity(connection, start);
            WriteGames(connection, games, random, start, end);
            var sessions = WriteSessions(connection, games, random, profile, start, end);
            allSessions = sessions;
            achievements = WriteAchievements(connection, games, sessions, random, end);
            WriteLibrary(connection, games, sessions, random, start, end);
            WriteOfficialPlaytime(connection, games, sessions, random, start, end);
            WriteScreenshots(connection, sessions, random);
            WriteProfileHistory(connection, random, start, end);
            WriteFriends(connection, games, random, profile, start, end);
            WriteRichPresence(connection, sessions, random);
            backlog = WriteInsightData(connection, games, sessions, profile, start, end);
            WriteNewArchives(connection, games, backlog, sessions, profile, start, end);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        await SqliteSchema.ExecuteAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE)", cancellationToken).ConfigureAwait(false);
        await WriteLibrarySnapshotAsync(options, games, backlog, allSessions, profile, cancellationToken).ConfigureAwait(false);

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

    private sealed record Session(int AppId, int Start, int Duration, bool Crash = false);

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

        // 连续闪退：替换掉最后几条，会话总数还是准确的。最近一周一定有一串，周刊才有东西可报
        var crashes = new List<Session>();
        for (var burst = 0; burst < Math.Max(1, profile.Sessions / 1000); burst++)
        {
            var at = burst == 0 ? end - 3 * 86400L : random.NextInt64(start, end - 86400);
            var game = games[picker.Next(random)];
            for (var attempt = 0; attempt < 3 + burst % 3; attempt++)
            {
                var duration = random.Next(15, 90);
                crashes.Add(new Session(game.AppId, (int)at, duration, true));
                at += duration + random.Next(20, 120);
            }
        }
        sessions.RemoveRange(sessions.Count - crashes.Count, crashes.Count);
        sessions.AddRange(crashes);

        using var record = new Inserter(connection, "use_app_record",
            "app_id", "steam_id", "start_time", "end_time", "duration", "last_heartbeat", "idle_duration",
            "foreground_duration", "device_id", "source", "end_reason", "note", "tags", "auto_tags");
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
                random.NextDouble() < 0.01 ? "开黑" : null,
                // 其余标签由 SessionTagService 算，闪退这里先写好，语义视图不运行服务也有数据
                session.Crash ? 16 : null);
        }
        return sessions;
    }

    /// <summary>
    /// 洞察用到的数据：商店状态、版本史，和一批买了没玩的积压游戏。用单独的随机数，不影响上面的档案。
    /// 返回积压游戏，它们只在游戏库快照里。
    /// </summary>
    private static List<Game> WriteInsightData(SqliteConnection connection, List<Game> games, List<Session> sessions, FakeArchiveProfile profile, long start, long end)
    {
        var random = new Random(profile.Seed + 1);
        var backlog = Enumerable.Range(0, Math.Max(12, profile.Games / 4)).Select(index =>
        {
            var id = FirstAppId + (games.Count + index) * 10;
            var studio = Studios[random.Next(Studios.Length)];
            return new Game(id, $"{Prefixes[(index + 7) % Prefixes.Length]} {Suffixes[(index + 3) % Suffixes.Length]} Deluxe", studio, studio, Genres[random.Next(Genres.Length)], 0, 0);
        }).ToList();

        using (var app = new Inserter(connection, "steam_app", "app_id", "name", "installed", "type", "developer", "publisher", "steam_release_date", "is_free_app"))
        using (var acquisition = new Inserter(connection, "library_acquisition", "account_name", "app_id", "package_id", "acquired_at"))
            foreach (var game in backlog)
            {
                app.Run(game.AppId, game.Name, 0, "game", game.Developer, game.Publisher, random.NextInt64(start - 86400L * 365 * 9, end), 0);
                acquisition.Run(AccountName, game.AppId, game.AppId / 10, random.NextInt64(start, end - 86400L * 30));
            }

        using (var store = new Inserter(connection, "app_store_status",
                   "app_id", "visible", "purchasable", "is_free", "price_cents", "review_count", "review_percent", "franchise", "release_date", "delisted_at", "checked_at"))
            foreach (var game in games.Concat(backlog))
            {
                var delisted = random.NextDouble() < 0.06;
                store.Run(
                    game.AppId,
                    delisted ? 0 : 1,
                    delisted ? 0 : 1,
                    0,
                    random.Next(4, 60) * 100 - 1,
                    (int)Math.Exp(random.NextDouble() * 9 + 3),
                    random.Next(38, 98),
                    random.NextDouble() < 0.3 ? game.Name.Split(' ')[0] : null,
                    random.NextInt64(start - 86400L * 365 * 9, end),
                    delisted ? end - random.Next(30, 300) * 86400L : null,
                    end);
            }

        using (var build = new Inserter(connection, "app_build_history", "app_id", "build_id", "observed_at", "is_baseline"))
            foreach (var game in games.Take(Math.Min(games.Count, 20)))
            {
                var buildId = random.Next(100_000, 900_000);
                build.Run(game.AppId, buildId, start, 1);
                for (var at = start + random.Next(20, 90) * 86400L; at < end; at += random.Next(20, 90) * 86400L)
                    build.Run(game.AppId, buildId += random.Next(1, 500), at, 0);
            }
        return backlog;
    }

    /// <summary>游戏库快照走真实的缓存格式，这样积压库和奇观榜读到的是和线上一样的数据。</summary>
    private static async Task WriteLibrarySnapshotAsync(
        DbContextOptions<AppDbContext> options,
        List<Game> games,
        List<Game> backlog,
        List<Session> sessions,
        FakeArchiveProfile profile,
        CancellationToken cancellationToken)
    {
        var minutes = sessions.GroupBy(session => session.AppId).ToDictionary(group => group.Key, group => group.Sum(session => session.Duration) / 60);
        var lastPlayed = sessions.GroupBy(session => session.AppId).ToDictionary(group => group.Key, group => group.Max(session => session.Start + session.Duration));
        var owned = games.Concat(backlog).Select(game => new SteamOwnedGame
        {
            AppId = game.AppId,
            Name = game.Name,
            NameLocalized = game.Name,
            PlaytimeForever = minutes.GetValueOrDefault(game.AppId),
            RtimeLastPlayed = lastPlayed.GetValueOrDefault(game.AppId),
            IsOwned = true
        }).ToList();
        var store = new SteamFeatureSnapshotStore(
            new EfSteamResourceCacheStore(new FixedContextFactory(options), NullLogger<EfSteamResourceCacheStore>.Instance),
            TimeProvider.System,
            NullLogger<SteamFeatureSnapshotStore>.Instance);
        await store.SaveLibraryAsync(AccountName, ulong.Parse(SteamId, CultureInfo.InvariantCulture), owned, SteamDataSource.Cm, profile.End, cancellationToken).ConfigureAwait(false);
    }

    private sealed class FixedContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
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
        var changes = 0;
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
                {
                    change.Run(item.AppId, item.AppId * 1000 + index, $"ACH_{index:D3}", $"里程碑 {index + 1}", "archived", null, null, end);
                    changes++;
                }
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
        // 绝版成就要解锁够多才出现，小档案里可能一条都没有，变更史不能是空的
        if (changes == 0) change.Run(games[0].AppId, 1, "ACH_NEW", "新增的成就", "added", null, "基础集", end);
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

    private static readonly string[] Maps = ["Lighthouse", "Old Quarry", "Sunken Station", "Harbor Gate", "Glass Canyon"];
    private static readonly string[] Modes = ["Ranked", "Casual", "Co-op"];
    private static readonly string[] Ranks = ["Bronze", "Silver", "Gold", "Platinum"];

    /// <summary>第八节的档案。用单独的随机数，已有数据不受影响。</summary>
    private static void WriteNewArchives(SqliteConnection connection, List<Game> games, List<Game> backlog, List<Session> sessions, FakeArchiveProfile profile, long start, long end)
    {
        var random = new Random(profile.Seed + 2);
        var played = sessions.GroupBy(session => session.AppId)
            .ToDictionary(group => group.Key, group => (Seconds: group.Sum(session => (long)session.Duration), First: group.Min(session => (long)session.Start), Last: group.Max(session => (long)session.Start)));
        var top = games.Where(game => played.ContainsKey(game.AppId)).OrderByDescending(game => played[game.AppId].Seconds).ToList();

        WriteStoreTags(connection, games, backlog);
        WritePlatformPlaytime(connection, top, played, random);
        WriteInstallHistory(connection, top, random, start, end);
        WritePlayerCounts(connection, top, random, end);
        WriteStoreArchive(connection, top, random, start, end);
        WriteWishlist(connection, backlog, random, start, end);
        WritePurchases(connection, top, backlog, random, start, end);
        WriteCommunity(connection, top, random, end);
        WriteFamily(connection, top, played, end);
        WriteRecordings(connection, top, sessions, random);
        WritePresenceDetail(connection, sessions, random);
        WritePioneer(connection, random);
        WritePalettes(connection, random);
        WriteFriendPlaytime(connection, top, profile, random, end);
        using var collector = new Inserter(connection, "collector_state", "id", "last_run_at", "last_success_at", "last_items");
        foreach (var id in new[] { "platform", "recordings", "wishlist", "family", "friendPlaytime", "players", "serverShutdown", "billing", "community" })
            collector.Run(id, end, end, random.Next(1, 40));
    }

    private static int[] TagsOf(Game game)
    {
        // 同一类型的游戏共用前几个标签，购前模拟才有相似的游戏可比
        var family = Array.IndexOf(Genres, game.Genre) * 10 + 100;
        return [family, family + 1, family + 2, 19 + game.AppId / 10 % 5, 21 + game.AppId / 10 % 7];
    }

    private static void WriteStoreTags(SqliteConnection connection, List<Game> games, List<Game> backlog)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE app_store_status SET tag_ids = $tags, category_ids = $categories WHERE app_id = $id";
        var tags = command.Parameters.Add("$tags", SqliteType.Text);
        var categories = command.Parameters.Add("$categories", SqliteType.Text);
        var id = command.Parameters.Add("$id", SqliteType.Integer);
        foreach (var game in games.Concat(backlog))
        {
            tags.Value = JsonSerializer.Serialize(TagsOf(game));
            categories.Value = game.Genre == "Shooter" ? "[1,36]" : "[2]";
            id.Value = game.AppId;
            command.ExecuteNonQuery();
        }
    }

    private static void WritePlatformPlaytime(SqliteConnection connection, List<Game> top, Dictionary<int, (long Seconds, long First, long Last)> played, Random random)
    {
        using var row = new Inserter(connection, "platform_playtime",
            "steam_id", "app_id", "observed_at", "windows_minutes", "mac_minutes", "linux_minutes", "deck_minutes", "disconnected_minutes",
            "first_played", "last_played", "first_windows", "first_linux", "first_deck", "last_windows", "last_linux", "last_deck");
        foreach (var game in top)
        {
            var (seconds, first, last) = played[game.AppId];
            var minutes = (int)(seconds / 60);
            var deck = random.NextDouble() < 0.3 ? (int)(minutes * random.NextDouble() * 0.5) : 0;
            var linux = random.NextDouble() < 0.4 ? (int)((minutes - deck) * random.NextDouble() * 0.6) : 0;
            var windows = minutes - deck - linux;
            var offline = random.NextDouble() < 0.1 ? random.Next(5, 120) : 0;
            row.Run(SteamId, game.AppId, last, windows, 0, linux, deck, offline, first, last,
                windows > 0 ? first : null, linux > 0 ? first + 86400 : null, deck > 0 ? first + 172800 : null,
                windows > 0 ? last : null, linux > 0 ? last - 3600 : null, deck > 0 ? last - 7200 : null);
        }
    }

    private static void WriteInstallHistory(SqliteConnection connection, List<Game> top, Random random, long start, long end)
    {
        using var row = new Inserter(connection, "app_install_event", "app_id", "kind", "observed_at", "size_on_disk", "build_id");
        foreach (var game in top.Take(40))
        {
            var size = random.NextInt64(2, 80) * 1_000_000_000;
            row.Run(game.AppId, "baseline", start, size, null);
            if (random.NextDouble() < 0.4) continue;
            var removed = random.NextInt64(start + 86400, end - 86400 * 40);
            row.Run(game.AppId, "uninstalled", removed, null, null);
            if (random.NextDouble() < 0.5) row.Run(game.AppId, "installed", removed + random.Next(5, 30) * 86400L, size, null);
        }
    }

    private static void WritePlayerCounts(SqliteConnection connection, List<Game> top, Random random, long end)
    {
        using var row = new Inserter(connection, "app_player_count", "app_id", "day", "peak", "low", "samples");
        foreach (var game in top.Take(15))
        {
            var level = random.Next(200, 60_000);
            for (var offset = 240; offset >= 0; offset--)
            {
                // 慢慢衰减，偶尔因为更新回升一截，周末多一点
                level = (int)(level * (random.NextDouble() < 0.02 ? 1.6 : 0.993));
                var day = DateTimeOffset.FromUnixTimeSeconds(end - offset * 86400L).UtcDateTime;
                var count = (int)(level * (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 1.25 : 1) * (0.9 + random.NextDouble() * 0.2));
                row.Run(game.AppId, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), count, (int)(count * 0.7), 1);
            }
        }
    }

    private static void WriteStoreArchive(SqliteConnection connection, List<Game> top, Random random, long start, long end)
    {
        using var snapshot = new Inserter(connection, "store_page_snapshot", "app_id", "captured_at", "fingerprint", "facts");
        using var artifact = new Inserter(connection, "app_artifact_event", "app_id", "kind", "observed_at", "before_value", "after_value");
        var first = start + 86400 * 30;
        for (var index = 0; index < Math.Min(top.Count, 40); index++)
        {
            var game = top[index];
            var facts = new SteamStat.Core.Archives.Store.StorePageFacts(
                game.Name, $"{game.Genre} from {game.Developer}.", [game.Developer], [game.Publisher], null, start, $"a{index:x4}/header.jpg", $"a{index:x4}/capsule.jpg", [], $"app/{game.AppId}");
            snapshot.Run(game.AppId, first, facts.Fingerprint(), facts.ToJson());
            if (index % 8 != 3) continue;
            var at = random.NextInt64(first + 86400, end);
            var renamed = facts with { Name = $"{game.Name}: Reforged", HeaderAsset = $"b{index:x4}/header.jpg" };
            snapshot.Run(game.AppId, at, renamed.Fingerprint(), renamed.ToJson());
            artifact.Run(game.AppId, "renamed", at, facts.Name, renamed.Name);
            artifact.Run(game.AppId, "coverChanged", at, facts.HeaderAsset, renamed.HeaderAsset);
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT app_id, delisted_at FROM app_store_status WHERE delisted_at IS NOT NULL ORDER BY app_id LIMIT 8";
            using var reader = command.ExecuteReader();
            var delisted = new List<(long AppId, long At)>();
            while (reader.Read()) delisted.Add((reader.GetInt64(0), reader.GetInt64(1)));
            reader.Close();
            foreach (var (appId, at) in delisted) artifact.Run(appId, "delisted", at, null, null);
        }
        if (top.Count > 5) artifact.Run(top[5].AppId, "serverShutdown", end - 86400L * 12, "news", "Important notice: servers will shut down on December 1");
    }

    private static void WriteWishlist(SqliteConnection connection, List<Game> backlog, Random random, long start, long end)
    {
        using var row = new Inserter(connection, "wishlist_record", "steam_id", "app_id", "added_at", "first_seen_at", "removed_at", "priority");
        using var snapshot = new Inserter(connection, "store_page_snapshot", "app_id", "captured_at", "fingerprint", "facts");
        // 已入库的积压游戏：加入时间早于入库时间，算「买到了」
        foreach (var game in backlog.Take(8))
            row.Run(SteamId, game.AppId, start, start, end - random.Next(10, 200) * 86400L, null);
        for (var index = 0; index < 14; index++)
        {
            var appId = FirstAppId + 500_000 + index * 10;
            var name = $"{Prefixes[(index + 3) % Prefixes.Length]} {Suffixes[(index + 11) % Suffixes.Length]}";
            var added = random.NextInt64(start, end - 86400L * 20);
            var removed = index % 5 == 4 ? random.NextInt64(added + 86400, end) : (long?)null;
            row.Run(SteamId, appId, added, added, removed, index);
            var facts = new SteamStat.Core.Archives.Store.StorePageFacts(name, "On the wishlist.", ["Tin Lantern"], ["Tin Lantern"], null, null, null, null, [], null);
            snapshot.Run(appId, added, facts.Fingerprint(), facts.ToJson());
        }
    }

    private static void WritePurchases(SqliteConnection connection, List<Game> top, List<Game> backlog, Random random, long start, long end)
    {
        using var row = new Inserter(connection, "purchase_record",
            "steam_id", "row_key", "date", "kind", "type_text", "total_cents", "currency", "wallet_change_cents", "payment_method", "refunded", "items", "first_seen_at");
        static string Items(params (string Name, int? AppId, long Cents)[] items)
            => JsonSerializer.Serialize(items.Select(item => new { name = item.Name, appId = item.AppId, source = item.AppId == null ? "none" : "exact", allocatedCents = item.Cents, shared = items.Length > 1 }));
        var key = 0;
        foreach (var game in top.Concat(backlog).Take(70))
        {
            var cents = random.Next(2, 30) * 1000 - 200L;
            var date = random.NextInt64(start, end) / 86400 * 86400;
            var refunded = key % 17 == 16;
            row.Run(SteamId, $"txn:{1000 + key++}", date, refunded ? "refund" : "purchase", refunded ? "Refund" : "Purchase", cents, "CNY", null, key % 3 == 0 ? "Alipay" : "WeChat Pay", refunded ? 1 : 0, Items((game.Name, game.AppId, cents)), date);
        }
        for (var index = 0; index < 18; index++)
        {
            var date = random.NextInt64(start, end) / 86400 * 86400;
            var cents = random.Next(1, 200) * 100L;
            row.Run(SteamId, $"row:market{index}", date, "market", "Market Transaction", cents, "CNY", -cents, null, 0, Items(("Steam Community Market", null, cents)), date);
        }
        for (var index = 0; index < 6; index++)
        {
            var date = random.NextInt64(start, end) / 86400 * 86400;
            row.Run(SteamId, $"txn:{5000 + index}", date, "wallet", "Purchase", 10000L, "CNY", 10000L, "Alipay", 0, Items(("¥100.00 Wallet Credit", null, 10000L)), date);
        }
    }

    private static void WriteCommunity(SqliteConnection connection, List<Game> top, Random random, long end)
    {
        using var review = new Inserter(connection, "community_review",
            "steam_id", "review_id", "app_id", "recommended", "hours_total", "hours_at_review", "posted_at", "text", "helpful", "funny", "first_seen_at", "last_seen_at", "missing_since");
        for (var index = 0; index < Math.Min(top.Count, 12); index++)
        {
            var posted = end - random.Next(20, 500) * 86400L;
            review.Run(SteamId, $"{70000 + index}", top[index].AppId, index % 4 == 3 ? 0 : 1, random.Next(20, 400) + 0.5, random.Next(5, 20) + 0.5, posted,
                index % 4 == 3 ? "前十小时很好，之后全是重复。" : "晚上打开，抬头就天亮了。", random.Next(0, 40), random.Next(0, 9), end - 86400L * 100, end - 86400L, index == 7 ? end - 86400L * 30 : null);
        }
        using var file = new Inserter(connection, "community_file",
            "steam_id", "kind", "published_file_id", "app_id", "title", "description", "author", "first_seen_at", "last_seen_at");
        for (var index = 0; index < Math.Min(top.Count, 8); index++)
            file.Run(SteamId, index % 3 == 0 ? "guides" : "favorites", $"{880000 + index}", top[index].AppId, index % 3 == 0 ? $"{top[index].Name} 新手路线" : $"{top[index].Name} 地图合集", "写给第一次打开这款游戏的人。", index % 3 == 0 ? PersonaName : FriendNames[index % FriendNames.Length], end - 86400L * 100, end - 86400L);
        using var comment = new Inserter(connection, "community_comment",
            "steam_id", "comment_id", "author_steam_id", "author_name", "posted_at", "text", "first_seen_at", "last_seen_at", "missing_since");
        for (var index = 0; index < 30; index++)
        {
            var posted = end - random.Next(1, 600) * 86400L;
            comment.Run(SteamId, $"{900000 + index}", null, FriendNames[index % FriendNames.Length], posted, index % 3 == 0 ? "今晚开黑吗" : "上次那局太离谱了", posted, end - 86400L, index == 4 ? end - 86400L * 12 : null);
        }
    }

    private static void WriteFamily(SqliteConnection connection, List<Game> top, Dictionary<int, (long Seconds, long First, long Last)> played, long end)
    {
        const string Group = "4100000000000001";
        const string Sibling = "76561198999000002";
        using var member = new Inserter(connection, "family_member", "family_group_id", "steam_id", "name", "role", "joined_at", "former", "updated_at");
        member.Run(Group, SteamId, PersonaName, 1, end - 86400L * 300, 0, end);
        member.Run(Group, Sibling, "示例家人", 1, end - 86400L * 280, 0, end);
        using var playtime = new Inserter(connection, "family_playtime", "family_group_id", "steam_id", "app_id", "by_owner", "first_played", "latest_played", "seconds", "updated_at");
        foreach (var game in top.Take(10))
        {
            var (seconds, first, last) = played[game.AppId];
            playtime.Run(Group, SteamId, game.AppId, 0, first, last, seconds, end);
            playtime.Run(Group, Sibling, game.AppId, 0, first + 86400, last - 86400, seconds / 3, end);
        }
    }

    private static void WriteRecordings(SqliteConnection connection, List<Game> top, List<Session> sessions, Random random)
    {
        using var timeline = new Inserter(connection, "recording_timeline", "account_id", "steam_id", "timeline_id", "app_id", "started_at", "duration_ms", "markers", "indexed_at");
        using var media = new Inserter(connection, "recording_media",
            "account_id", "steam_id", "media_id", "kind", "app_id", "started_at", "duration_ms", "size_bytes", "name", "timeline_ids", "folder_path", "markers", "indexed_at", "missing_since");
        var ids = top.Take(6).Select(game => game.AppId).ToHashSet();
        var picked = sessions.Where(session => ids.Contains(session.AppId) && session.Duration > 1800).TakeLast(14).ToArray();
        for (var index = 0; index < picked.Length; index++)
        {
            var session = picked[index];
            var stamp = DateTimeOffset.FromUnixTimeSeconds(session.Start).UtcDateTime.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var id = $"timeline_{session.AppId}{stamp}";
            var markers = Enumerable.Range(0, random.Next(3, 9)).Select(offset => new
            {
                kind = offset == 0 ? "phase" : offset % 3 == 0 ? "achievement" : offset % 3 == 1 ? "tag" : "usermarker",
                title = offset % 3 == 1 ? Maps[offset % Maps.Length] : offset % 3 == 0 ? "首次通关" : "精彩瞬间",
                group = offset % 3 == 1 ? "Map" : null,
                icon = (string?)null,
                offsetMs = offset * (long)(session.Duration / 9) * 1000,
                durationMs = (long?)null
            }).ToArray();
            var json = JsonSerializer.Serialize(markers);
            timeline.Run(AccountId, SteamId, id, session.AppId, session.Start, session.Duration * 1000L, json, session.Start);
            if (index % 2 != 0) continue;
            var clip = index % 4 == 0;
            var name = $"{(clip ? "clip" : "bg")}_{session.AppId}_{stamp}";
            media.Run(AccountId, SteamId, name, clip ? "clip" : "background", session.AppId, session.Start, clip ? 45_000L : session.Duration * 1000L, clip ? 80_000_000L : 2_400_000_000L,
                clip ? "这一枪" : null, JsonSerializer.Serialize(new[] { id }), $"/fake/gamerecordings/{(clip ? "clips" : "video")}/{name}", clip ? json : "[]", session.Start, index >= 8 ? session.Start + 86400L * 7 : null);
        }
    }

    /// <summary>带地图、模式、段位的游戏内状态，只给最常玩的游戏。</summary>
    private static void WritePresenceDetail(SqliteConnection connection, List<Session> sessions, Random random)
    {
        using var record = new Inserter(connection, "rich_presence_record",
            "account_name", "steam_id", "is_self", "app_id", "raw_values", "display_text", "source", "observed_at");
        var favorite = sessions.GroupBy(session => session.AppId).OrderByDescending(group => group.Sum(session => (long)session.Duration)).First();
        var rank = 0;
        foreach (var session in favorite.TakeLast(120))
        {
            if (random.NextDouble() < 0.08) rank = Math.Min(rank + 1, Ranks.Length - 1);
            var values = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["game:map"] = Maps[random.Next(Maps.Length)],
                ["game:mode"] = Modes[random.Next(Modes.Length)],
                ["rank"] = Ranks[rank],
                ["steam_player_group_size"] = random.Next(1, 5).ToString(CultureInfo.InvariantCulture)
            };
            var text = $"{values["game:mode"]} - {values["game:map"]}";
            record.Run(AccountName, SteamId, 1, session.AppId, JsonSerializer.Serialize(values), text, "push", session.Start + 90);
            record.Run(AccountName, SteamId, 1, session.AppId, "{}", null, "poll", session.Start + session.Duration);
        }
    }

    private static void WritePioneer(SqliteConnection connection, Random random)
    {
        using var select = connection.CreateCommand();
        select.CommandText = "SELECT id, unlock_time, global_percent FROM achievement_unlock_archive WHERE global_percent IS NOT NULL ORDER BY id DESC LIMIT 400";
        var rows = new List<(long Id, long Unlock, double Percent)>();
        using (var reader = select.ExecuteReader())
            while (reader.Read()) rows.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetDouble(2)));
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE achievement_unlock_archive SET first_global_percent = $percent, first_percent_at = $at WHERE id = $id";
        var percent = update.Parameters.Add("$percent", SqliteType.Real);
        var at = update.Parameters.Add("$at", SqliteType.Integer);
        var id = update.Parameters.Add("$id", SqliteType.Integer);
        foreach (var row in rows)
        {
            percent.Value = row.Percent * (0.1 + random.NextDouble() * 0.8);
            at.Value = row.Unlock + 3600;
            id.Value = row.Id;
            update.ExecuteNonQuery();
        }
    }

    private static void WritePalettes(SqliteConnection connection, Random random)
    {
        using var select = connection.CreateCommand();
        select.CommandText = "SELECT id FROM screenshot_archive ORDER BY id";
        var ids = new List<long>();
        using (var reader = select.ExecuteReader())
            while (reader.Read()) ids.Add(reader.GetInt64(0));
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE screenshot_archive SET palette = $palette WHERE id = $id";
        var palette = update.Parameters.Add("$palette", SqliteType.Text);
        var id = update.Parameters.Add("$id", SqliteType.Integer);
        foreach (var value in ids.Where(_ => random.NextDouble() < 0.9))
        {
            palette.Value = string.Join(',', Enumerable.Range(0, 3).Select(_ => $"#{random.Next(0x202020, 0xE0E0E0):x6}"));
            id.Value = value;
            update.ExecuteNonQuery();
        }
    }

    private static void WriteFriendPlaytime(SqliteConnection connection, List<Game> top, FakeArchiveProfile profile, Random random, long end)
    {
        using var select = connection.CreateCommand();
        select.CommandText = "SELECT friend_steam_id FROM friend_tracking ORDER BY friend_steam_id LIMIT 3";
        var friends = new List<string>();
        using (var reader = select.ExecuteReader())
            while (reader.Read()) friends.Add(reader.GetString(0));
        using var row = new Inserter(connection, "friend_playtime_snapshot", "steam_id", "app_id", "playtime_minutes", "observed_at");
        foreach (var friend in friends)
        {
            foreach (var game in top.Take(8))
            {
                var minutes = random.Next(60, 6000);
                row.Run(friend, game.AppId, minutes, end - 86400L * 90);
                for (var days = 80; days >= 0 && random.NextDouble() < 0.85; days -= random.Next(5, 20))
                    row.Run(friend, game.AppId, minutes += random.Next(10, 300), end - days * 86400L);
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
