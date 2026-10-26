using FluentAssertions;
using SteamStat.Core.Features.Achievements;
using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class AchievementMergeKeyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1700000000);

    [Test]
    public void KeyOnlyUnlock_MatchesSchemaByInternalKeyWithoutName()
    {
        var result = SteamAchievementMerge.Compose(
            1,
            "english",
            SchemaResult(Schema(Def("ACH_A", 11), Def("ACH_B", 12))),
            UnlockResult(
                Unlock(12, string.Empty, true, Now),
                Unlock(11, string.Empty, false, null)));

        result.Achievements.Select(entry => entry.IsUnlocked).Should().Equal(false, true);
        result.Achievements[1].UnlockTimeUtc.Should().Be(Now);
        result.UnmatchedUnlockNames.Should().BeEmpty();
        result.Summary.Unlocked.Should().Be(1);
        result.Summary.Unknown.Should().Be(0);
    }

    [Test]
    public void KeyMatch_WinsOverConflictingName()
    {
        var result = SteamAchievementMerge.Compose(
            1,
            "english",
            SchemaResult(Schema(Def("ACH_A", 11))),
            UnlockResult(Unlock(11, "OTHER_NAME", true, Now)));

        result.Achievements.Should().ContainSingle().Which.IsUnlocked.Should().BeTrue();
        result.UnmatchedUnlockNames.Should().BeEmpty();
    }

    [Test]
    public void KeylessUnlock_MatchesByOrdinalName()
    {
        var result = SteamAchievementMerge.Compose(
            1,
            "english",
            SchemaResult(Schema(Def("ACH_A"), Def("ACH_B", 7))),
            UnlockResult(Unlock(null, "ACH_B", true, Now)));

        result.Achievements[0].IsUnlocked.Should().BeNull();
        result.Achievements[1].IsUnlocked.Should().BeTrue();
        result.UnmatchedUnlockNames.Should().BeEmpty();
    }

    [Test]
    public void DuplicateUnlockKeys_AreConservativelyUnmatched()
    {
        var result = SteamAchievementMerge.Compose(
            1,
            "english",
            SchemaResult(Schema(Def("ACH_A", 11))),
            UnlockResult(
                Unlock(11, string.Empty, true, Now),
                Unlock(11, string.Empty, false, null)));

        result.Achievements.Should().ContainSingle().Which.IsUnlocked.Should().BeNull();
        result.UnmatchedUnlockNames.Should().Equal("internal-key:11", "internal-key:11");
    }

    [Test]
    public void KeyMatch_TakesPriorityOverKeylessNameCollision()
    {
        var result = SteamAchievementMerge.Compose(
            1,
            "english",
            SchemaResult(Schema(Def("ACH_A", 11))),
            UnlockResult(
                Unlock(11, string.Empty, true, Now),
                Unlock(null, "ACH_A", false, null)));

        result.Achievements.Should().ContainSingle().Which.IsUnlocked.Should().BeTrue();
        result.Achievements[0].UnlockTimeUtc.Should().Be(Now);
        result.UnmatchedUnlockNames.Should().Equal("ACH_A");
    }

    [Test]
    public void UnmatchedNamelessUnlock_ReportsInternalKeyDiagnostic()
    {
        var result = SteamAchievementMerge.Compose(
            1,
            "english",
            SchemaResult(Schema(Def("ACH_A", 11))),
            UnlockResult(Unlock(77, string.Empty, true, Now)));

        result.Achievements.Should().ContainSingle().Which.IsUnlocked.Should().BeNull();
        result.UnmatchedUnlockNames.Should().Equal("internal-key:77");
    }

    [Test]
    public void UnlockMatchedOnce_EvenWhenMultipleDefinitionsShareName()
    {
        var result = SteamAchievementMerge.Compose(
            1,
            "english",
            SchemaResult(Schema(Def("ACH_A", 11), Def("ACH_A", 12))),
            UnlockResult(Unlock(null, "ACH_A", true, Now)));

        result.Achievements.Select(entry => entry.IsUnlocked).Should().Equal(new bool?[] { true, null });
        result.UnmatchedUnlockNames.Should().BeEmpty();
    }

    private static SteamAchievementDefinition Def(string internalName, uint? internalKey = null)
        => new(
            internalKey,
            internalName,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            false,
            null,
            null,
            false,
            SteamAchievementProgressType.None,
            null,
            null);

    private static SteamAchievementSchemaSnapshot Schema(params SteamAchievementDefinition[] definitions)
        => new(1, "english", 1, 0, definitions, []);

    private static SteamAchievementUnlock Unlock(
        uint? internalKey,
        string internalName,
        bool? isUnlocked,
        DateTimeOffset? unlockTimeUtc)
        => new(internalKey, internalName, isUnlocked, unlockTimeUtc);

    private static SteamGatewayResult<SteamAchievementSchemaSnapshot> SchemaResult(
        SteamAchievementSchemaSnapshot snapshot)
        => SteamGatewayResult<SteamAchievementSchemaSnapshot>.Succeeded(
            snapshot, SteamDataSource.Cm, SteamFreshness.Fresh, Now, Now);

    private static SteamGatewayResult<SteamAchievementUnlockSnapshot> UnlockResult(
        params SteamAchievementUnlock[] entries)
        => SteamGatewayResult<SteamAchievementUnlockSnapshot>.Succeeded(
            new SteamAchievementUnlockSnapshot(100000001UL, 1, 1, entries),
            SteamDataSource.Cm,
            SteamFreshness.Fresh,
            Now,
            Now);
}
