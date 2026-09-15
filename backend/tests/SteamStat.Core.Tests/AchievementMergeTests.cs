using FluentAssertions;
using SteamStat.Core.Features.Achievements;
using SteamStat.Core.Steam.Gateway;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class AchievementMergeTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1700000000);

    [Test]
    public void FullSuccess_MergesUnlocksByInternalName()
    {
        var result = SteamAchievementMerge.Compose(
            1,
            "english",
            SchemaResult(Schema(Def("ACH_A", 1), Def("ACH_B", 2), Def("ACH_C", 3))),
            UnlockResult(
                Unlock(1, "ACH_A", true, Now),
                Unlock(2, "ACH_B", true, Now.AddMinutes(5))));

        result.IsSuccess.Should().BeTrue();
        result.IsEmpty.Should().BeFalse();
        result.IsStale.Should().BeFalse();
        result.IsPartial.Should().BeFalse();
        result.Achievements.Should().HaveCount(3);
        result.Achievements[0].IsUnlocked.Should().BeTrue();
        result.Achievements[0].UnlockTimeUtc.Should().Be(Now);
        result.Achievements[1].IsUnlocked.Should().BeTrue();
        result.Achievements[2].IsUnlocked.Should().BeNull();
        result.Summary.Total.Should().Be(3);
        result.Summary.Unlocked.Should().Be(2);
        result.Summary.Unknown.Should().Be(1);
        result.Summary.Percentage.Should().BeApproximately(66.67, 0.01);
        result.UnmatchedUnlockNames.Should().BeEmpty();
    }

    [Test]
    public void EmptySchema_IsSuccessEmpty()
    {
        var result = SteamAchievementMerge.Compose(
            1,
            "english",
            SchemaResult(Schema()),
            UnlockResult());

        result.IsSuccess.Should().BeTrue();
        result.IsEmpty.Should().BeTrue();
        result.Achievements.Should().BeEmpty();
        result.Summary.Should().Be(new SteamAchievementSummary(0, 0, 0, null));
        result.UnmatchedUnlockNames.Should().BeEmpty();
    }

    [Test]
    public void SchemaFailure_IsNeverEmptyAndCarriesFailureMetadata()
    {
        var result = SteamAchievementMerge.Compose(
            1,
            "english",
            SteamGatewayResult<SteamAchievementSchemaSnapshot>.Failed(
                SteamFailureKind.Offline, "achievement_schema_session_unavailable"),
            UnlockResult(Unlock(1, "ACH_A", true, Now)));

        result.IsSuccess.Should().BeFalse();
        result.IsEmpty.Should().BeFalse();
        result.Schema.Should().BeNull();
        result.Achievements.Should().BeEmpty();
        result.Summary.Should().Be(new SteamAchievementSummary(0, 0, 0, null));
        result.UnmatchedUnlockNames.Should().BeEmpty();
        result.SchemaState.Failure.Should().Be(SteamFailureKind.Offline);
        result.SchemaState.DiagnosticCode.Should().Be("achievement_schema_session_unavailable");
        result.SchemaState.HasValue.Should().BeFalse();
    }

    [Test]
    public void StaleSchema_KeepsValueFreshnessAndFailureMetadata()
    {
        var lastSuccess = Now.AddDays(-2);
        var result = SteamAchievementMerge.Compose(
            1,
            "english",
            SteamGatewayResult<SteamAchievementSchemaSnapshot>.Succeeded(
                Schema(Def("ACH_A", 1)),
                SteamDataSource.Sqlite,
                SteamFreshness.Stale,
                lastSuccess,
                lastSuccess,
                SteamFailureKind.Offline,
                SteamAchievementDiagnosticCodes.SchemaSessionUnavailable),
            UnlockResult(Unlock(1, "ACH_A", true, Now)));

        result.IsSuccess.Should().BeTrue();
        result.IsStale.Should().BeTrue();
        result.IsPartial.Should().BeFalse();
        result.SchemaState.Failure.Should().Be(SteamFailureKind.Offline);
        result.SchemaState.DiagnosticCode.Should()
            .Be(SteamAchievementDiagnosticCodes.SchemaSessionUnavailable);
        result.SchemaState.LastSuccessfulUpdate.Should().Be(lastSuccess);
        result.Summary.Unlocked.Should().Be(1);
    }

    [Test]
    public void MissingProgress_ExposesDefinitionsAsUnknownPartialResult()
    {
        var result = SteamAchievementMerge.Compose(
            1,
            "english",
            SchemaResult(Schema(Def("ACH_A", 1), Def("ACH_B", 2))),
            SteamGatewayResult<SteamAchievementUnlockSnapshot>.Failed(
                SteamFailureKind.AuthenticationRequired,
                SteamAchievementDiagnosticCodes.ProgressSessionUnavailable));

        result.IsSuccess.Should().BeTrue();
        result.IsPartial.Should().BeTrue();
        result.Achievements.Should().OnlyContain(entry => entry.IsUnlocked == null);
        result.Summary.Unknown.Should().Be(2);
        result.ProgressState.Failure.Should().Be(SteamFailureKind.AuthenticationRequired);
        result.ProgressState.HasValue.Should().BeFalse();
    }

    [Test]
    public void StaleProgressWithFailureMetadata_IsNotPartial()
    {
        var lastSuccess = Now.AddMinutes(-30);
        var result = SteamAchievementMerge.Compose(
            1,
            "english",
            SchemaResult(Schema(Def("ACH_A", 1))),
            SteamGatewayResult<SteamAchievementUnlockSnapshot>.Succeeded(
                new SteamAchievementUnlockSnapshot(100000001UL, 1, 1, [Unlock(1, "ACH_A", true, Now)]),
                SteamDataSource.Sqlite,
                SteamFreshness.Stale,
                lastSuccess,
                lastSuccess,
                SteamFailureKind.Timeout,
                SteamAchievementDiagnosticCodes.UserStatsFailed));

        result.IsPartial.Should().BeFalse();
        result.ProgressState.Failure.Should().Be(SteamFailureKind.Timeout);
        result.ProgressState.HasValue.Should().BeTrue();
        result.Summary.Unlocked.Should().Be(1);
    }

    [Test]
    public void LocalizedName_IsNeverUsedAsMergeKey()
    {
        var result = SteamAchievementMerge.Compose(
            1,
            "english",
            SchemaResult(Schema(Def("ACH_A", 1, localizedName: "Alpha"))),
            UnlockResult(Unlock(null, "Alpha", true, Now)));

        result.Achievements.Should().ContainSingle().Which.IsUnlocked.Should().BeNull();
        result.UnmatchedUnlockNames.Should().Equal("Alpha");
        result.Summary.Unknown.Should().Be(1);
    }

    [Test]
    public void InternalNameMatch_IsOrdinalCaseSensitive()
    {
        var result = SteamAchievementMerge.Compose(
            1,
            "english",
            SchemaResult(Schema(Def("ACH_A", 1))),
            UnlockResult(Unlock(1, "ach_a", true, Now)));

        result.Achievements.Should().ContainSingle().Which.IsUnlocked.Should().BeNull();
        result.UnmatchedUnlockNames.Should().Equal("ach_a");
    }

    [Test]
    public void UnlockOrder_DoesNotAffectMatching()
    {
        var result = SteamAchievementMerge.Compose(
            1,
            "english",
            SchemaResult(Schema(Def("ACH_A", 1), Def("ACH_B", 2))),
            UnlockResult(
                Unlock(2, "ACH_B", true, Now),
                Unlock(1, "ACH_A", false, null)));

        result.Achievements[0].IsUnlocked.Should().BeFalse();
        result.Achievements[1].IsUnlocked.Should().BeTrue();
        result.Summary.Unlocked.Should().Be(1);
    }

    [Test]
    public void InternalKeyConflict_DoesNotTrustTheUnlock()
    {
        var result = SteamAchievementMerge.Compose(
            1,
            "english",
            SchemaResult(Schema(Def("ACH_A", 1))),
            UnlockResult(Unlock(2, "ACH_A", true, Now)));

        result.Achievements.Should().ContainSingle().Which.IsUnlocked.Should().BeNull();
        result.UnmatchedUnlockNames.Should().Equal("ACH_A");
        result.Summary.Unknown.Should().Be(1);
    }

    [Test]
    public void InternalKeyAgreement_OrMissingKey_StillMatches()
    {
        var result = SteamAchievementMerge.Compose(
            1,
            "english",
            SchemaResult(Schema(Def("ACH_A", 1), Def("ACH_B"), Def("ACH_C", 5))),
            UnlockResult(
                Unlock(1, "ACH_A", true, Now),
                Unlock(9, "ACH_B", false, null),
                Unlock(null, "ACH_C", true, Now)));

        result.UnmatchedUnlockNames.Should().BeEmpty();
        result.Achievements.Select(entry => entry.IsUnlocked).Should().Equal(true, false, true);
    }

    [Test]
    public void HiddenDefinitions_AreRevealedOnlyWhenUnlocked()
    {
        var hidden = Def("ACH_HIDDEN", hidden: true);
        var visible = Def("ACH_VISIBLE");

        new SteamAchievementEntry(hidden, true, Now).IsRevealed.Should().BeTrue();
        new SteamAchievementEntry(hidden, false, null).IsRevealed.Should().BeFalse();
        new SteamAchievementEntry(hidden, null, null).IsRevealed.Should().BeFalse();
        new SteamAchievementEntry(visible, false, null).IsRevealed.Should().BeTrue();
    }

    [Test]
    public void OverviewResult_IsPartialOnlyForProgressPartialDiagnostic()
    {
        var partial = new SteamAchievementOverviewResult(
            "acct",
            [],
            new SteamAchievementResourceState(
                SteamDataSource.Cm,
                SteamFreshness.Fresh,
                Now,
                null,
                SteamAchievementDiagnosticCodes.ProgressPartial));
        partial.IsPartial.Should().BeTrue();

        var other = new SteamAchievementOverviewResult(
            "acct",
            [],
            new SteamAchievementResourceState(
                null, null, null, SteamFailureKind.Offline, "achievement_progress_cache_miss"));
        other.IsPartial.Should().BeFalse();
    }

    private static SteamAchievementDefinition Def(
        string internalName,
        uint? internalKey = null,
        bool hidden = false,
        string localizedName = "")
        => new(
            internalKey,
            internalName,
            localizedName,
            string.Empty,
            string.Empty,
            string.Empty,
            hidden,
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
