using FluentAssertions;
using SteamStat.Core.Features.Achievements;
using SteamStat.Core.Features.Achievements.Contracts;
using SteamStat.Core.PublicData;
using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Core.Tests;

/// <summary>成就集的完成度口径。游戏的形状取自线上 GetGameAchievements 的实测结果。</summary>
[TestFixture]
public sealed class AchievementSetsTests
{
    private const uint NorthernLords = 1303183;
    private const uint RoyalCourt = 1303182;
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    private static readonly SteamAppOwnership OwnsRoyalCourt = new(new HashSet<uint> { RoyalCourt }, true);

    private static SteamAchievementGroup Group(
        uint id, string name = "", uint? dlc = null, bool archived = false, bool developerOnly = false, bool isPublic = true, uint order = 0)
        => new(id, name, dlc, archived, developerOnly, isPublic, order);

    private static SteamAchievementDefinition Def(string name, uint? group = 0, bool archived = false, bool hidden = false)
        => new(null, name, name, "", "", "", hidden, null, group, archived, SteamAchievementProgressType.None, null, null);

    private static IEnumerable<SteamAchievementDefinition> Many(string prefix, int count, uint group)
        => Enumerable.Range(0, count).Select(index => Def($"{prefix}{index}", group));

    private static SteamAchievementGameResult Compose(
        IReadOnlyList<SteamAchievementDefinition> definitions,
        IReadOnlyList<SteamAchievementGroup> groups,
        IEnumerable<string> unlocked,
        SteamAppOwnership? ownership = null)
    {
        var unlockedSet = unlocked.ToHashSet();
        var entries = definitions
            .Select((definition, index) => new SteamAchievementUnlock(
                null,
                definition.InternalName,
                unlockedSet.Contains(definition.InternalName),
                unlockedSet.Contains(definition.InternalName) ? Now.AddMinutes(index) : null))
            .ToArray();
        return SteamAchievementMerge.Compose(
            1158310,
            "english",
            SteamGatewayResult<SteamAchievementSchemaSnapshot>.Succeeded(
                new SteamAchievementSchemaSnapshot(1158310, "english", 1, 1, definitions, groups),
                SteamDataSource.Cm, SteamFreshness.Fresh, Now, Now),
            SteamGatewayResult<SteamAchievementUnlockSnapshot>.Succeeded(
                new SteamAchievementUnlockSnapshot(1, 1158310, 1, entries),
                SteamDataSource.Cm, SteamFreshness.Fresh, Now, Now),
            ownership);
    }

    private static (SteamAchievementDefinition[] Definitions, SteamAchievementGroup[] Groups) CrusaderKings()
        => (
            [.. Many("BASE", 56, 0), .. Many("NL", 10, 1), .. Many("RC", 20, 2)],
            [Group(0), Group(1, "Northern Lords", NorthernLords), Group(2, "Royal Court", RoyalCourt)]);

    [Test]
    public void CompletedBaseGame_IsNotHeldBackByDlcYouDoNotOwn()
    {
        var (definitions, groups) = CrusaderKings();

        var result = Compose(definitions, groups, definitions.Where(item => item.GroupId == 0).Select(item => item.InternalName), new SteamAppOwnership(new HashSet<uint>(), true));

        result.Summary.Total.Should().Be(56);
        result.Summary.Unlocked.Should().Be(56);
        result.Summary.Percentage.Should().Be(100);
        result.Summary.AllTotal.Should().Be(86);
        result.Summary.AllUnlocked.Should().Be(56);
        result.OwnershipKnown.Should().BeTrue();
    }

    [Test]
    public void OwnedDlcCounts_EvenBeforeAnyUnlock()
    {
        var (definitions, groups) = CrusaderKings();

        var result = Compose(definitions, groups, [], OwnsRoyalCourt);

        result.Summary.Total.Should().Be(76);
        var royalCourt = result.Sets.Single(set => set.DlcAppId == RoyalCourt);
        (royalCourt.Counted, royalCourt.Owned).Should().Be((true, true));
        var northernLords = result.Sets.Single(set => set.DlcAppId == NorthernLords);
        (northernLords.Counted, northernLords.Owned).Should().Be((false, false));
        northernLords.Total.Should().Be(10);
    }

    [Test]
    public void ExactOwnership_ExcludesADlcYouNoLongerOwnEvenWithUnlocks()
    {
        var (definitions, groups) = CrusaderKings();

        var result = Compose(definitions, groups, ["NL0"], OwnsRoyalCourt);

        result.Sets.Single(set => set.DlcAppId == NorthernLords).Counted.Should().BeFalse();
        result.Summary.Unlocked.Should().Be(0);
        result.Summary.AllUnlocked.Should().Be(1);
    }

    [Test]
    public void WithoutOwnership_ADlcCountsOnceAnyAchievementInItIsUnlocked()
    {
        var (definitions, groups) = CrusaderKings();

        var result = Compose(definitions, groups, ["NL0"]);

        result.OwnershipKnown.Should().BeFalse();
        var northernLords = result.Sets.Single(set => set.DlcAppId == NorthernLords);
        (northernLords.Counted, northernLords.Owned).Should().Be((true, null));
        result.Sets.Single(set => set.DlcAppId == RoyalCourt).Counted.Should().BeFalse();
        result.Summary.Total.Should().Be(66);
    }

    [Test]
    public void PartialOwnership_TrustsWhatIsKnownAndInfersTheRest()
    {
        var (definitions, groups) = CrusaderKings();

        var result = Compose(definitions, groups, ["NL0"], new SteamAppOwnership(new HashSet<uint> { RoyalCourt }, false));

        result.OwnershipKnown.Should().BeFalse();
        result.Sets.Single(set => set.DlcAppId == RoyalCourt).Owned.Should().BeTrue();
        var northernLords = result.Sets.Single(set => set.DlcAppId == NorthernLords);
        (northernLords.Counted, northernLords.Owned).Should().Be((true, null));
    }

    [Test]
    public void ArchivedAchievement_CountsOnlyIfYouHoldIt()
    {
        // 军团要塞 2 的 Replay 组：total 8，completion 4
        SteamAchievementDefinition[] definitions =
        [
            .. Many("LIVE", 4, 11),
            .. Enumerable.Range(0, 4).Select(index => Def($"OLD{index}", 11, archived: true, hidden: index == 3))
        ];
        SteamAchievementGroup[] groups = [Group(11, "Replay")];

        var missed = Compose(definitions, groups, ["LIVE0", "LIVE1", "LIVE2", "LIVE3"]);
        missed.Summary.Total.Should().Be(4);
        missed.Sets.Single().Completed.Should().BeTrue();
        missed.Summary.AllTotal.Should().Be(8);
        missed.Achievements.Single(entry => entry.Definition.InternalName == "OLD0").IsRevealed.Should().BeFalse();
        missed.Achievements.Single(entry => entry.Definition.InternalName == "OLD0").IsCounted.Should().BeFalse();

        var held = Compose(definitions, groups, ["LIVE0", "OLD3"]);
        held.Summary.Total.Should().Be(5);
        held.Summary.Unlocked.Should().Be(2);
        var old = held.Achievements.Single(entry => entry.Definition.InternalName == "OLD3");
        (old.IsArchived, old.IsCounted, old.IsRevealed).Should().Be((true, true, true));
    }

    [Test]
    public void ArchivedSet_ExistsOnlyForPlayersWhoUnlockedSomethingInIt()
    {
        SteamAchievementDefinition[] definitions = [Def("A"), Def("EVENT0", 1), Def("EVENT1", 1)];
        SteamAchievementGroup[] groups = [Group(0), Group(1, "Event", archived: true)];

        Compose(definitions, groups, []).Sets.Should().ContainSingle().Which.GroupId.Should().Be(0);

        var held = Compose(definitions, groups, ["EVENT1"]);
        var set = held.Sets.Single(item => item.GroupId == 1);
        (set.Archived, set.Total, set.Unlocked, set.AllTotal).Should().Be((true, 1, 1, 2));
        held.Summary.Total.Should().Be(2);
    }

    [Test]
    public void DeveloperAndPrivateGroups_DisappearCompletely()
    {
        SteamAchievementDefinition[] definitions = [Def("A"), Def("DEV", 1), Def("PRIVATE", 2)];
        SteamAchievementGroup[] groups = [Group(0), Group(1, "Dev", developerOnly: true), Group(2, "Private", isPublic: false)];

        var result = Compose(definitions, groups, ["DEV", "PRIVATE"]);

        result.Achievements.Should().ContainSingle();
        result.Sets.Should().ContainSingle();
        result.Summary.AllTotal.Should().Be(1);
        result.UnmatchedUnlockNames.Should().BeEmpty();
    }

    [Test]
    public void EmptyBaseGroup_IsLeftOutAndSetsFollowOrderThenId()
    {
        // 《传送门 2》：基础集为空，单人 order 0，合作 order 1
        SteamAchievementDefinition[] definitions = [.. Many("COOP", 22, 1), .. Many("SOLO", 29, 2)];
        SteamAchievementGroup[] groups = [Group(0), Group(1, "Co-op", order: 1), Group(2, "Single Player", order: 0)];

        var result = Compose(definitions, groups, []);

        result.Sets.Select(set => set.GroupId).Should().Equal(2u, 1u);
        result.Summary.Total.Should().Be(51);
    }

    [Test]
    public void SchemaWithoutGroups_BehavesLikeASingleBaseSet()
    {
        var result = Compose([Def("A", null), Def("B", null)], [], ["A"]);

        result.Sets.Should().ContainSingle().Which.GroupId.Should().Be(0u);
        result.Summary.Should().Be(new SteamAchievementSummary(2, 1, 0, 50, 2, 1));
    }

    [Test]
    public void CompletedAt_IsTheLastUnlockInTheSet()
    {
        var result = Compose([Def("A"), Def("B")], [Group(0)], ["A", "B"]);

        result.Sets.Single().CompletedAt.Should().Be(Now.AddMinutes(1));
    }

    [Test]
    public void PublicDataRecord_KeepsGroupsInTheChosenLanguage()
    {
        var record = new PublicAchievementRecord
        {
            Languages = new()
            {
                ["english"] = [new PublicAchievementItem { Name = "A", GroupId = 1 }, new PublicAchievementItem { Name = "B", GroupId = 0, Archived = true }]
            },
            Groups = new()
            {
                ["english"] = [new PublicAchievementGroup { Id = 0 }, new PublicAchievementGroup { Id = 1, Name = "Royal Court", DlcAppId = RoyalCourt }]
            }
        };

        var schema = PublicDataMapper.ToSnapshot(1, "schinese", record)!;

        schema.Definitions[0].GroupId.Should().Be(1u);
        schema.Definitions[1].Archived.Should().BeTrue();
        schema.Groups.Should().HaveCount(2);
        schema.Groups[1].DlcAppId.Should().Be(RoyalCourt);
        schema.Groups[1].IsPublic.Should().BeTrue();
    }

    [Test]
    public void PublicDataRecord_WithoutGroupsStillWorks()
    {
        var record = new PublicAchievementRecord
        {
            Languages = new() { ["english"] = [new PublicAchievementItem { Name = "A" }] }
        };

        var schema = PublicDataMapper.ToSnapshot(1, "english", record)!;

        schema.Groups.Should().BeEmpty();
        schema.Definitions[0].GroupId.Should().BeNull();
    }
}
