using FluentAssertions;
using SteamStat.Core.Features.Achievements;
using SteamStat.Core.Features.Achievements.Contracts;
using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Core.Tests;

/// <summary>
/// 多账号总账。每个账号的结果都由 <see cref="SteamAchievementMerge.Compose"/> 算出，
/// 再转成档案里的行，和 Host 写档案的方式一致。
/// </summary>
[TestFixture]
public sealed class AchievementLedgerTests
{
    private const uint AppId = 1158310;
    private const uint NorthernLords = 1303183;
    private const uint RoyalCourt = 1303182;
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private static SteamAchievementGroup Group(uint id, string name = "", uint? dlc = null, bool archived = false)
        => new(id, name, dlc, archived, false, true, 0);

    private static SteamAchievementDefinition Def(string name, uint group = 0, bool archived = false)
        => new(null, name, name, "", "", "", false, null, group, archived, SteamAchievementProgressType.None, null, null);

    private static IEnumerable<SteamAchievementDefinition> Many(string prefix, int count, uint group, bool archived = false)
        => Enumerable.Range(0, count).Select(index => Def($"{prefix}{index}", group, archived));

    /// <summary>本体 10 个（其中 2 个已归档）、两个 DLC 集各 5 个。</summary>
    private static (SteamAchievementDefinition[] Definitions, SteamAchievementGroup[] Groups) Game()
        => (
            [.. Many("BASE", 8, 0), .. Many("OLD", 2, 0, archived: true), .. Many("NL", 5, 1), .. Many("RC", 5, 2)],
            [Group(0), Group(1, "Northern Lords", NorthernLords), Group(2, "Royal Court", RoyalCourt)]);

    private sealed record Account(string Name, SteamAchievementGameResult Result);

    private static Account Play(
        string accountName,
        IEnumerable<string> unlocked,
        SteamAppOwnership? ownership,
        int minuteOffset = 0)
    {
        var (definitions, groups) = Game();
        var unlockedSet = unlocked.ToHashSet();
        var entries = definitions
            .Select((definition, index) => new SteamAchievementUnlock(
                null,
                definition.InternalName,
                unlockedSet.Contains(definition.InternalName),
                unlockedSet.Contains(definition.InternalName) ? Now.AddMinutes(index + minuteOffset) : null))
            .ToArray();
        var result = SteamAchievementMerge.Compose(
            AppId,
            "english",
            SteamGatewayResult<SteamAchievementSchemaSnapshot>.Succeeded(
                new SteamAchievementSchemaSnapshot(AppId, "english", 1, 1, definitions, groups),
                SteamDataSource.Cm, SteamFreshness.Fresh, Now, Now),
            SteamGatewayResult<SteamAchievementUnlockSnapshot>.Succeeded(
                new SteamAchievementUnlockSnapshot(1, AppId, 1, entries),
                SteamDataSource.Cm, SteamFreshness.Fresh, Now, Now),
            ownership);
        return new Account(accountName, result);
    }

    private static SteamAppOwnership Owns(params uint[] dlc) => new(dlc.ToHashSet(), true);

    private static LedgerGame Ledger(params Account[] accounts)
        => AchievementLedger.Merge(
            accounts.Select(account => new LedgerGameRow(
                account.Name, AppId, "Crusader Kings", account.Result.Summary.Total, account.Result.Summary.Unlocked,
                account.Result.Summary.AllTotal, account.Result.Summary.AllUnlocked)),
            accounts.SelectMany(account => account.Result.Sets.Select(set => new LedgerSetRow(
                account.Name, AppId, set.GroupId, set.LocalizedName, set.DlcAppId, set.Archived, set.Counted, set.Owned,
                set.Total, set.Unlocked, set.AllTotal, set.Completed, set.CompletedAt?.ToUnixTimeSeconds(), 1))),
            accounts.SelectMany(account => account.Result.Achievements
                .Where(entry => entry.IsUnlocked == true)
                .Select(entry => new LedgerUnlockRow(
                    account.Name, AppId, entry.Definition.InternalName, entry.Definition.GroupId, entry.IsArchived,
                    entry.UnlockTimeUtc!.Value.ToUnixTimeSeconds()))))
            .Single();

    private static IEnumerable<string> Names(string prefix, int count) => Enumerable.Range(0, count).Select(index => $"{prefix}{index}");

    [Test]
    public void OneAccount_KeepsItsOwnNumbers()
    {
        var alice = Play("alice", [.. Names("BASE", 8), "OLD0", .. Names("RC", 3)], Owns(RoyalCourt));

        var ledger = Ledger(alice);

        ledger.Total.Should().Be(alice.Result.Summary.Total);
        ledger.Unlocked.Should().Be(alice.Result.Summary.Unlocked);
        ledger.AllTotal.Should().Be(alice.Result.Summary.AllTotal);
        ledger.AllUnlocked.Should().Be(alice.Result.Summary.AllUnlocked);
        ledger.Sets.Select(set => (set.GroupId, set.Total, set.Unlocked, set.Counted, set.Completed))
            .Should().Equal(alice.Result.Sets.Select(set => (set.GroupId, set.Total, set.Unlocked, set.Counted, set.Completed)));
        ledger.Synthetic.Should().BeFalse();
    }

    [Test]
    public void Union_MatchesComposingTheUnionDirectly()
    {
        var alice = Play("alice", [.. Names("BASE", 5), "OLD0", .. Names("NL", 2)], Owns(NorthernLords));
        var bob = Play("bob", [.. Names("BASE", 8).Skip(3), "OLD1", .. Names("RC", 5)], Owns(RoyalCourt));
        var union = Play("union", [.. Names("BASE", 8), "OLD0", "OLD1", .. Names("NL", 2), .. Names("RC", 5)], Owns(NorthernLords, RoyalCourt));

        var ledger = Ledger(alice, bob);

        ledger.Total.Should().Be(union.Result.Summary.Total);
        ledger.Unlocked.Should().Be(union.Result.Summary.Unlocked);
        ledger.AllUnlocked.Should().Be(union.Result.Summary.AllUnlocked);
        ledger.Sets.Select(set => (set.GroupId, set.Total, set.Unlocked, set.Counted, set.Owned))
            .Should().Equal(union.Result.Sets.Select(set => (set.GroupId, set.Total, set.Unlocked, set.Counted, set.Owned)));
    }

    [Test]
    public void ArchivedAchievements_CountOnceEachWhoeverEarnedThem()
    {
        var alice = Play("alice", ["OLD0"], Owns());
        var bob = Play("bob", ["OLD0", "OLD1"], Owns());

        var baseSet = Ledger(alice, bob).Sets.Single(set => set.GroupId == 0);

        baseSet.Total.Should().Be(10, "8 个未归档 + 两人合起来获得的 2 个归档成就");
        baseSet.Unlocked.Should().Be(2);
    }

    [Test]
    public void DlcOwnedByEitherAccount_Counts()
    {
        var alice = Play("alice", [], Owns(NorthernLords));
        var bob = Play("bob", [], Owns(RoyalCourt));

        var ledger = Ledger(alice, bob);

        ledger.Sets.Where(set => set.DlcAppId != null).Should().OnlyContain(set => set.Counted && set.Owned == true);
        ledger.Total.Should().Be(8 + 5 + 5);
    }

    [Test]
    public void CompletionOnlyTheUnionHas_IsMarkedSynthetic()
    {
        var alice = Play("alice", Names("BASE", 4), Owns(), minuteOffset: 100);
        var bob = Play("bob", Names("BASE", 8).Skip(4), Owns());

        var ledger = Ledger(alice, bob);

        ledger.Completed.Should().BeTrue();
        ledger.Synthetic.Should().BeTrue("两个账号在 Steam 上都没有全成就");
        ledger.Accounts.Should().OnlyContain(account => !account.Completed);
        ledger.Sets.Single(set => set.GroupId == 0).Synthetic.Should().BeTrue();
        // 每个成就取最早一次，凑齐的时刻是其中最晚的：alice 的 BASE3（第 103 分钟）
        ledger.CompletedAt.Should().Be(Now.AddMinutes(103).ToUnixTimeSeconds());
    }

    [Test]
    public void CompletionOneAccountHas_IsNotSynthetic()
    {
        var alice = Play("alice", Names("BASE", 8), Owns());
        var bob = Play("bob", Names("BASE", 2), Owns(), minuteOffset: -60);

        var ledger = Ledger(alice, bob);

        ledger.Completed.Should().BeTrue();
        ledger.Synthetic.Should().BeFalse();
        ledger.Accounts.Single(account => account.AccountName == "alice").Completed.Should().BeTrue();
    }

    [Test]
    public void EarliestUnlockWins_ForTheCompletionTime()
    {
        var alice = Play("alice", Names("BASE", 8), Owns(), minuteOffset: 1000);
        var bob = Play("bob", Names("BASE", 8), Owns());

        Ledger(alice, bob).CompletedAt.Should().Be(Now.AddMinutes(7).ToUnixTimeSeconds(), "bob 先完成");
    }

    [Test]
    public void StickyCompletion_IsKeptAfterNewAchievements()
    {
        // 档案里的集已完成，之后 Steam 加了一个成就：7 / 8
        var set = new LedgerSetRow("alice", AppId, 0, "", null, false, true, null, 8, 7, 10, true, 5, 1);

        var ledger = AchievementLedger.Merge(
            [new LedgerGameRow("alice", AppId, "x", 8, 7, 10, 7), new LedgerGameRow("bob", AppId, "x", 8, 1, 10, 1)],
            [set, set with { AccountName = "bob", Unlocked = 1, Completed = false, CompletedAt = null }],
            Names("BASE", 7).Select(name => new LedgerUnlockRow("alice", AppId, name, 0, false, 1))).Single();

        ledger.Completed.Should().BeTrue("集完成后再加成就，Steam 保留完成状态");
        ledger.Synthetic.Should().BeFalse();
        ledger.Unlocked.Should().Be(7);
        ledger.CompletedAt.Should().Be(5);
    }

    [Test]
    public void LegacyRows_FallBackToTheLargestTotalAndTheUnion()
    {
        var ledger = AchievementLedger.Merge(
            [new LedgerGameRow("alice", AppId, "x", 4, 2, null, null), new LedgerGameRow("bob", AppId, "", 4, 2, null, null)],
            [],
            [
                new LedgerUnlockRow("alice", AppId, "A", null, false, 10),
                new LedgerUnlockRow("alice", AppId, "B", null, false, 20),
                new LedgerUnlockRow("bob", AppId, "C", null, false, 30),
                new LedgerUnlockRow("bob", AppId, "D", null, false, 5),
                new LedgerUnlockRow("bob", AppId, "A", null, false, 1)
            ]).Single();

        ledger.Total.Should().Be(4);
        ledger.Unlocked.Should().Be(4);
        ledger.Completed.Should().BeTrue();
        ledger.Synthetic.Should().BeTrue();
        ledger.CompletedAt.Should().Be(30);
        ledger.AllTotal.Should().BeNull();
        ledger.AppName.Should().Be("x");
    }

    [Test]
    public void GroupOfAnOldUnlock_ComesFromAnotherAccount()
    {
        var ledger = AchievementLedger.Merge(
            [new LedgerGameRow("alice", AppId, "x", 1, 1, 2, 1), new LedgerGameRow("bob", AppId, "x", 1, 0, null, null)],
            [
                new LedgerSetRow("alice", AppId, 0, "", null, false, true, null, 1, 1, 1, true, 1, 1),
                new LedgerSetRow("alice", AppId, 1, "DLC", 9, false, false, false, 1, 0, 1, false, null, 1)
            ],
            [
                new LedgerUnlockRow("alice", AppId, "BASE", 0, false, 1),
                new LedgerUnlockRow("bob", AppId, "DLC0", null, false, 2),
                new LedgerUnlockRow("alice", AppId, "DLC0", 1, false, 3)
            ]).Single();

        ledger.Sets.Single(set => set.GroupId == 1).Unlocked.Should().Be(1);
        ledger.Sets.Single(set => set.GroupId == 0).Unlocked.Should().Be(1);
    }
}
