using ElectronNet.Features.Friends.Persistence;
using ElectronNet.Models;
using ElectronNet.Services;
using ElectronNet.Tests.Features.Insights;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Features;

namespace ElectronNet.Tests.Features.Friends;

/// <summary>
/// CM 会话重连后，没变的好友状态会被再报一遍。记录器要和落库的最后一条比较，
/// 已经写进去的重复记录由一次性迁移清掉。
/// </summary>
[TestFixture]
public sealed class FriendStatusRecordDedupTests : InsightHostFixture
{
    private const string Pal = "76561198000000009";
    private static readonly FriendStatusValue Offline = new(PersonaState: 0);
    private static readonly FriendStatusValue Online = new(PersonaState: 1);
    private static readonly FriendStatusValue NotPlaying = new(GameId: "0", GameName: "");

    private FriendStatusRecordService _recorder = null!;

    [SetUp]
    public void SetUpRecorder()
        => _recorder = new FriendStatusRecordService(DbFactory, Clock, NullLogger<FriendStatusRecordService>.Instance);

    [TearDown]
    public void TearDownRecorder() => _recorder.Dispose();

    [Test]
    public async Task SameValueAsTheLastRecord_IsNotWrittenAgain()
    {
        await _recorder.RecordChangeAsync("me", Pal, "Pal", "state", Offline, Online);
        // 重连：内存里的基线回到「离线」，好友其实一直在线
        await _recorder.RecordChangeAsync("me", Pal, "Pal", "state", Offline, Online);
        await _recorder.RecordChangeAsync("me", Pal, "Pal", "state", Offline, Online);

        (await RecordsAsync()).Should().ContainSingle();
    }

    [Test]
    public async Task ChangeMissedWhileDisconnected_UsesTheLastRecordAsThePreviousValue()
    {
        await _recorder.RecordChangeAsync("me", Pal, "Pal", "state", Offline, Online);
        // 断线期间好友下线了。重连后对账：内存里是「离线 → 离线」，落库的最后一条是在线
        await _recorder.RecordChangeAsync("me", Pal, "Pal", "state", Offline, Offline);

        var records = await RecordsAsync();
        records.Should().HaveCount(2);
        records[1].PreviousValue.Should().Be("""{"personaState":1}""");
        records[1].CurrentValue.Should().Be("""{"personaState":0}""");
    }

    [Test]
    public async Task ReconciliationWithoutHistoryOrChange_WritesNothing()
    {
        await _recorder.RecordChangeAsync("me", Pal, "Pal", "state", Online, Online);
        await _recorder.RecordChangeAsync("me", Pal, "Pal", "game", NotPlaying, NotPlaying);

        (await RecordsAsync()).Should().BeEmpty();
    }

    [Test]
    public async Task GameIsComparedByIdOnly_SoALateNameDoesNotCountAsAChange()
    {
        await _recorder.RecordChangeAsync("me", Pal, "Pal", "game", NotPlaying, new FriendStatusValue(GameId: "730", GameName: ""));
        await _recorder.RecordChangeAsync("me", Pal, "Pal", "game", NotPlaying, new FriendStatusValue(GameId: "730", GameName: "Counter-Strike 2"));
        await _recorder.RecordChangeAsync("me", Pal, "Pal", "game", NotPlaying, new FriendStatusValue(GameId: "570", GameName: "Dota 2"));

        var records = await RecordsAsync();
        records.Should().HaveCount(2);
        records[1].PreviousValue.Should().Contain("\"730\"");
    }

    [Test]
    public async Task OtherFriendsAccountsAndTypes_KeepTheirOwnBaseline()
    {
        await _recorder.RecordChangeAsync("me", Pal, "Pal", "state", Offline, Online);
        await _recorder.RecordChangeAsync("me", "76561198000000010", "Buddy", "state", Offline, Online);
        await _recorder.RecordChangeAsync("family", Pal, "Pal", "state", Offline, Online);
        await _recorder.RecordChangeAsync("me", Pal, "Pal", "relationship", new FriendStatusValue(Relationship: 0), new FriendStatusValue(Relationship: 3));

        (await RecordsAsync()).Should().HaveCount(4);
    }

    [Test]
    public async Task Cleanup_KeepsTheFirstRecordOfEachRun()
    {
        await using (var db = await DbFactory.CreateDbContextAsync())
        {
            var time = 1000;
            void Add(string type, string? current, string friend = Pal, string account = "me")
                => db.FriendStatusRecordTable.Add(new FriendStatusRecord
                {
                    AccountName = account, FriendSteamId = friend, FriendPersonaName = "Pal", ChangeType = type,
                    PreviousValue = """{"personaState":0}""", CurrentValue = current, Timestamp = time++
                });
            // 在线 ×3（两条重复）、离线、在线：留 3 条
            Add("state", """{"personaState":1}""");
            Add("state", """{"personaState":1}""");
            Add("game", """{"gameId":"730","gameName":""}""");
            Add("state", """{"personaState":1}""");
            Add("state", """{"personaState":0}""");
            Add("state", """{"personaState":1}""");
            // 同一款游戏，名字后来才有：算重复；换游戏、退出不算
            Add("game", """{"gameId":"730","gameName":"Counter-Strike 2"}""");
            Add("game", """{"gameId":"0","gameName":""}""");
            Add("game", """{"gameId":"0"}""");
            // 游戏内状态：重连时同一句话又记了一遍算重复，换了一句、再换回来都不算
            Add("richPresence", """{"richPresence":"在主菜单"}""");
            Add("richPresence", """{"richPresence":"在主菜单"}""");
            Add("richPresence", """{"richPresence":"排位赛"}""");
            Add("richPresence", """{"richPresence":"在主菜单"}""");
            // 别的好友、别的账号各算各的
            Add("state", """{"personaState":1}""", friend: "76561198000000010");
            Add("state", """{"personaState":1}""", account: "family");
            // 格式异常和没有值的记录保持原样
            Add("state", "not json");
            Add("state", "not json");
            Add("state", null);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync(FriendStatusRecordCleanup.Sql);
        }

        var records = await RecordsAsync();
        records.Where(record => record.AccountName == "me" && record.FriendSteamId == Pal && record.ChangeType == "state" && record.CurrentValue?.StartsWith('{') == true)
            .Select(record => record.CurrentValue)
            .Should().Equal("""{"personaState":1}""", """{"personaState":0}""", """{"personaState":1}""");
        records.Where(record => record.ChangeType == "game").Select(record => record.CurrentValue)
            .Should().Equal("""{"gameId":"730","gameName":""}""", """{"gameId":"0","gameName":""}""");
        records.Where(record => record.ChangeType == "richPresence").Select(record => record.CurrentValue)
            .Should().Equal("""{"richPresence":"在主菜单"}""", """{"richPresence":"排位赛"}""", """{"richPresence":"在主菜单"}""");
        records.Should().HaveCount(3 + 2 + 3 + 2 + 3);
    }

    [Test]
    public async Task ObservationWindows_AreInferredFromRecordsBeforeTheFirstRealWindow()
    {
        const long tail = FriendStatusRecordService.InferredWindowSeconds;
        await using (var db = await DbFactory.CreateDbContextAsync())
        {
            void Add(string account, long time)
                => db.FriendStatusRecordTable.Add(new FriendStatusRecord
                {
                    AccountName = account, FriendSteamId = Pal, FriendPersonaName = "Pal", ChangeType = "state",
                    CurrentValue = """{"personaState":1}""", Timestamp = time
                });
            // 挨得近的两条合成一段；隔得远的另起一段；碰到第一个真实窗口就停；真实窗口之后的不推算
            Add("me", 1000);
            Add("me", 1000 + tail / 2);
            Add("me", 100_000);
            Add("me", 200_000 - 60);
            Add("me", 300_000);
            // 没有真实窗口的账号全靠推算
            Add("old", 5000);
            db.FriendObservationWindowTable.Add(new FriendObservationWindow { AccountName = "me", StartedAt = 200_000, EndedAt = 400_000 });
            await db.SaveChangesAsync();
        }

        var windows = (await _recorder.GetObservationWindowsAsync(null)).Windows
            .Select(window => (window.AccountName, window.StartedAt, window.EndedAt));
        windows.Should().Equal(
            ("me", 1000L, 1000 + tail / 2 + tail),
            ("old", 5000L, 5000 + tail),
            ("me", 100_000L, 100_000 + tail),
            ("me", 200_000L - 60, 200_000L),
            ("me", 200_000L, 400_000L));

        (await _recorder.GetObservationWindowsAsync(new FriendObservationWindowsRequest { AccountNames = ["old"] })).Windows
            .Should().ContainSingle().Which.StartedAt.Should().Be(5000);
    }

    private async Task<List<FriendStatusRecord>> RecordsAsync()
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        return await db.FriendStatusRecordTable.AsNoTracking().OrderBy(record => record.Id).ToListAsync();
    }
}
