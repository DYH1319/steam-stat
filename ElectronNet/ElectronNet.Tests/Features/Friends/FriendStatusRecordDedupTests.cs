using ElectronNet.Features.Friends.Persistence;
using ElectronNet.Models;
using ElectronNet.Services;
using ElectronNet.Tests.Features.Insights;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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
        records.Should().HaveCount(3 + 2 + 2 + 3);
    }

    private async Task<List<FriendStatusRecord>> RecordsAsync()
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        return await db.FriendStatusRecordTable.AsNoTracking().OrderBy(record => record.Id).ToListAsync();
    }
}
