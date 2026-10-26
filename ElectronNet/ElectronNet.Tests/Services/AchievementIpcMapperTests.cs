using System.Text.Json;
using ElectronNet.Hosting;
using FluentAssertions;
using SteamStat.Core.Features.Achievements;
using SteamStat.Core.Steam.Gateway;

namespace ElectronNet.Tests.Services;

[TestFixture]
public sealed class AchievementIpcMapperTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [Test]
    public void OverviewResult_MapsItemsProgressAndResourceState()
    {
        var result = new SteamAchievementOverviewResult(
            "acct",
            [
                new SteamAchievementOverviewItem(
                    10,
                    "Alpha",
                    "Alpha Localized",
                    120,
                    1699999999,
                    new SteamAchievementAppProgressSnapshot(10, 8, 4, 50.0)),
                new SteamAchievementOverviewItem(20, "Beta", "Beta Localized", 30, 1699999950, null)
            ],
            new SteamAchievementResourceState(
                SteamDataSource.Cm,
                SteamFreshness.Fresh,
                Now,
                SteamFailureKind.Transient,
                SteamAchievementDiagnosticCodes.ProgressPartial));

        var dto = IpcDtoMapper.ToDto(result);

        dto.Status.Should().Be("success");
        dto.AccountName.Should().Be("acct");
        dto.Partial.Should().BeTrue();
        dto.Source.Should().Be("cm");
        dto.Freshness.Should().Be("fresh");
        dto.LastSuccessfulUpdate.Should().Be(Now.ToUnixTimeSeconds());
        dto.Failure.Should().Be("transient");
        dto.DiagnosticCode.Should().Be(SteamAchievementDiagnosticCodes.ProgressPartial);
        dto.Games.Should().HaveCount(2);
        var first = dto.Games[0];
        first.AppId.Should().Be(10u);
        first.Name.Should().Be("Alpha");
        first.LocalizedName.Should().Be("Alpha Localized");
        first.PlaytimeForever.Should().Be(120);
        first.LastPlayedAt.Should().Be(1699999999L);
        first.Progress.Should().NotBeNull();
        first.Progress!.AppId.Should().Be(10u);
        first.Progress.Total.Should().Be(8);
        first.Progress.Unlocked.Should().Be(4);
        first.Progress.Percentage.Should().Be(50.0);
        dto.Games[1].Progress.Should().BeNull();
    }

    [Test]
    public void GameResult_FullSuccess_MapsEveryFieldWithStableWireEnums()
    {
        var unlockTime = Now.AddDays(-3);
        var lastSchemaSuccess = Now.AddDays(-1);
        var definitions = new[]
        {
            Def(
                "ACH_A",
                1,
                SteamAchievementProgressType.Int,
                localizedName: "Alpha",
                localizedDescription: "Desc A",
                globalPercent: 42.5,
                groupId: 7,
                min: 0,
                max: 100),
            Def("ACH_B", 2, SteamAchievementProgressType.Float, hidden: true),
            Def("ACH_C", null, SteamAchievementProgressType.None),
            Def("ACH_D", 4, SteamAchievementProgressType.Unknown, hidden: true, archived: true)
        };
        var groups = new[] { new SteamAchievementGroup(7, "Group Seven", 999, true, false, true, 3) };
        var schema = new SteamAchievementSchemaSnapshot(730, "english", 4, 0xABCDEFu, definitions, groups);
        var entries = new[]
        {
            new SteamAchievementEntry(definitions[0], true, unlockTime),
            new SteamAchievementEntry(definitions[1], false, null),
            new SteamAchievementEntry(definitions[2], null, null),
            new SteamAchievementEntry(definitions[3], true, unlockTime)
        };
        var result = new SteamAchievementGameResult(
            730,
            "english",
            schema,
            entries,
            new SteamAchievementSummary(4, 2, 1, 50.0),
            new SteamAchievementResourceState(
                SteamDataSource.Sqlite,
                SteamFreshness.Stale,
                lastSchemaSuccess,
                SteamFailureKind.Offline,
                SteamAchievementDiagnosticCodes.SchemaCacheMiss),
            new SteamAchievementResourceState(SteamDataSource.Cm, SteamFreshness.Fresh, Now, null, null),
            [])
        {
            AppName = "Sample Game"
        };

        var dto = IpcDtoMapper.ToDto(result);

        dto.Status.Should().Be("success");
        dto.AppId.Should().Be(730u);
        dto.AppName.Should().Be("Sample Game");
        dto.Language.Should().Be("english");
        dto.SchemaHash.Should().Be(0xABCDEFu);
        dto.Source.Should().Be("sqlite");
        dto.Freshness.Should().Be("stale");
        dto.LastSuccessfulUpdate.Should().Be(lastSchemaSuccess.ToUnixTimeSeconds());
        dto.Partial.Should().BeFalse();
        dto.Stale.Should().BeTrue();
        dto.Failure.Should().Be("offline");
        dto.DiagnosticCode.Should().Be(SteamAchievementDiagnosticCodes.SchemaCacheMiss);

        dto.Achievements.Should().HaveCount(4);
        var first = dto.Achievements[0];
        first.InternalKey.Should().Be(1u);
        first.InternalName.Should().Be("ACH_A");
        first.LocalizedName.Should().Be("Alpha");
        first.LocalizedDescription.Should().Be("Desc A");
        first.Icon.Should().Be("icon_ach_a");
        first.IconGray.Should().Be("icon_gray_ach_a");
        first.Hidden.Should().BeFalse();
        first.GlobalUnlockedPercent.Should().Be(42.5);
        first.GroupId.Should().Be(7u);
        first.Archived.Should().BeFalse();
        first.ProgressType.Should().Be("int");
        first.MinProgress.Should().Be(0.0);
        first.MaxProgress.Should().Be(100.0);
        first.IsUnlocked.Should().BeTrue();
        first.UnlockTimeUtc.Should().Be(unlockTime.ToUnixTimeSeconds());
        first.IsRevealed.Should().BeTrue();
        dto.Achievements[1].ProgressType.Should().Be("float");
        dto.Achievements[1].IsRevealed.Should().BeFalse();
        dto.Achievements[2].ProgressType.Should().Be("none");
        dto.Achievements[2].InternalKey.Should().BeNull();
        dto.Achievements[2].IsUnlocked.Should().BeNull();
        dto.Achievements[2].UnlockTimeUtc.Should().BeNull();
        dto.Achievements[3].ProgressType.Should().Be("unknown");
        dto.Achievements[3].Archived.Should().BeTrue();
        dto.Achievements[3].IsRevealed.Should().BeTrue();

        dto.Groups.Should().ContainSingle();
        var group = dto.Groups[0];
        group.GroupId.Should().Be(7u);
        group.LocalizedName.Should().Be("Group Seven");
        group.DlcAppId.Should().Be(999u);
        group.Archived.Should().BeTrue();
        group.DeveloperOnly.Should().BeFalse();
        group.IsPublic.Should().BeTrue();
        group.Order.Should().Be(3u);

        dto.Summary.Total.Should().Be(4);
        dto.Summary.Unlocked.Should().Be(2);
        dto.Summary.Unknown.Should().Be(1);
        dto.Summary.Percentage.Should().Be(50.0);

        dto.ProgressState.Source.Should().Be("cm");
        dto.ProgressState.Freshness.Should().Be("fresh");
        dto.ProgressState.LastSuccessfulUpdate.Should().Be(Now.ToUnixTimeSeconds());
        dto.ProgressState.Failure.Should().BeNull();
        dto.ProgressState.DiagnosticCode.Should().BeNull();
        dto.ProgressState.HasValue.Should().BeTrue();
    }

    [Test]
    public void GameResult_SchemaFailure_MapsTypedFailureWithoutExceptionText()
    {
        var result = new SteamAchievementGameResult(
            730,
            "english",
            null,
            [],
            new SteamAchievementSummary(0, 0, 0, null),
            new SteamAchievementResourceState(
                null,
                null,
                null,
                SteamFailureKind.NotFound,
                SteamAchievementDiagnosticCodes.AppUnavailable),
            new SteamAchievementResourceState(
                null,
                null,
                null,
                SteamFailureKind.NotFound,
                SteamAchievementDiagnosticCodes.AppUnavailable),
            []);

        var dto = IpcDtoMapper.ToDto(result);

        dto.Status.Should().Be("failure");
        dto.AppId.Should().Be(730u);
        dto.AppName.Should().BeEmpty();
        dto.SchemaHash.Should().BeNull();
        dto.Achievements.Should().BeEmpty();
        dto.Groups.Should().BeEmpty();
        dto.Source.Should().BeNull();
        dto.Freshness.Should().BeNull();
        dto.LastSuccessfulUpdate.Should().BeNull();
        dto.Partial.Should().BeFalse();
        dto.Stale.Should().BeFalse();
        dto.Failure.Should().Be("notFound");
        dto.DiagnosticCode.Should().Be(SteamAchievementDiagnosticCodes.AppUnavailable);
        dto.ProgressState.Failure.Should().Be("notFound");
        dto.ProgressState.DiagnosticCode.Should().Be(SteamAchievementDiagnosticCodes.AppUnavailable);
        dto.ProgressState.HasValue.Should().BeFalse();
        JsonSerializer.Serialize(dto).Should().NotContain("Exception").And.NotContain("StackTrace");
    }

    [Test]
    public void GameResult_PartialProgress_MapsSuccessWithTypedProgressFailure()
    {
        var definitions = new[] { Def("ACH_A", 1, SteamAchievementProgressType.None), Def("ACH_B", 2, SteamAchievementProgressType.None) };
        var schema = new SteamAchievementSchemaSnapshot(730, "english", 1, 7u, definitions, []);
        var entries = definitions.Select(definition => new SteamAchievementEntry(definition, null, null)).ToArray();
        var result = new SteamAchievementGameResult(
            730,
            "english",
            schema,
            entries,
            new SteamAchievementSummary(2, 0, 2, 0.0),
            new SteamAchievementResourceState(SteamDataSource.Cm, SteamFreshness.Fresh, Now, null, null),
            new SteamAchievementResourceState(
                null,
                null,
                null,
                SteamFailureKind.AuthenticationRequired,
                SteamAchievementDiagnosticCodes.ProgressSessionUnavailable),
            [])
        {
            AppName = "Sample Game"
        };

        var dto = IpcDtoMapper.ToDto(result);

        dto.Status.Should().Be("success");
        dto.Partial.Should().BeTrue();
        dto.Source.Should().Be("cm");
        dto.Failure.Should().BeNull();
        dto.Achievements.Should().OnlyContain(achievement => achievement.IsUnlocked == null);
        dto.Summary.Unknown.Should().Be(2);
        dto.ProgressState.Source.Should().BeNull();
        dto.ProgressState.Failure.Should().Be("authenticationRequired");
        dto.ProgressState.DiagnosticCode.Should()
            .Be(SteamAchievementDiagnosticCodes.ProgressSessionUnavailable);
        dto.ProgressState.HasValue.Should().BeFalse();
    }

    [Test]
    public void GameResult_EmptySchema_StaysSuccessWithEmptyAchievements()
    {
        var schema = new SteamAchievementSchemaSnapshot(730, "english", 1, 7u, [], []);
        var result = new SteamAchievementGameResult(
            730,
            "english",
            schema,
            [],
            new SteamAchievementSummary(0, 0, 0, null),
            new SteamAchievementResourceState(SteamDataSource.Http, SteamFreshness.Fresh, Now, null, null),
            new SteamAchievementResourceState(SteamDataSource.Cm, SteamFreshness.Fresh, Now, null, null),
            []);

        var dto = IpcDtoMapper.ToDto(result);

        dto.Status.Should().Be("success");
        dto.Achievements.Should().BeEmpty();
        dto.Groups.Should().BeEmpty();
        dto.Summary.Should().BeEquivalentTo(new { Total = 0, Unlocked = 0, Unknown = 0, Percentage = (double?)null });
        dto.Source.Should().Be("http");
        dto.Partial.Should().BeFalse();
    }

    private static SteamAchievementDefinition Def(
        string internalName,
        uint? internalKey,
        SteamAchievementProgressType progressType,
        bool hidden = false,
        bool archived = false,
        string localizedName = "",
        string localizedDescription = "",
        double? globalPercent = null,
        uint? groupId = null,
        double? min = null,
        double? max = null)
        => new(
            internalKey,
            internalName,
            localizedName,
            localizedDescription,
            $"icon_{internalName.ToLowerInvariant()}",
            $"icon_gray_{internalName.ToLowerInvariant()}",
            hidden,
            globalPercent,
            groupId,
            archived,
            progressType,
            min,
            max);
}
