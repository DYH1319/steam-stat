using FluentAssertions;
using SteamKit2.Internal;
using SteamStat.Core.Steam.Gateway.Internal;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class AchievementProgressMapperTests
{
    [Test]
    public void ProgressSummaries_MapEveryField()
    {
        var snapshots = AchievementProtocolMapper.MapProgressSummaries(
        [
            new CPlayer_GetAchievementsProgress_Response.AchievementProgress
            {
                appid = 1,
                unlocked = 3,
                total = 7,
                percentage = 42.5f
            },
            new CPlayer_GetAchievementsProgress_Response.AchievementProgress
            {
                appid = 2,
                unlocked = 0,
                total = 0,
                percentage = 0f
            }
        ]);

        snapshots.Should().HaveCount(2);
        snapshots[0].AppId.Should().Be(1u);
        snapshots[0].Total.Should().Be(7);
        snapshots[0].Unlocked.Should().Be(3);
        snapshots[0].Percentage.Should().Be(42.5);
        snapshots[1].AppId.Should().Be(2u);
        snapshots[1].Total.Should().Be(0);
        snapshots[1].Unlocked.Should().Be(0);
    }

    [Test]
    public void ProgressSummaries_RejectInvalidItems()
    {
        ((Action)(() => AchievementProtocolMapper.MapProgressSummaries(
                [
                    new CPlayer_GetAchievementsProgress_Response.AchievementProgress
                    {
                        appid = 1,
                        unlocked = 0,
                        total = 1
                    },
                    new CPlayer_GetAchievementsProgress_Response.AchievementProgress
                    {
                        appid = 1,
                        unlocked = 0,
                        total = 0
                    }
                ])))
            .Should().Throw<InvalidDataException>();
        ((Action)(() => AchievementProtocolMapper.MapProgressSummaries(
                [
                    new CPlayer_GetAchievementsProgress_Response.AchievementProgress
                    {
                        appid = 1,
                        unlocked = 5,
                        total = 2
                    }
                ])))
            .Should().Throw<InvalidDataException>();
    }

    [Test]
    public void ProgressSummaries_RejectOutOfRangeItems()
    {
        ((Action)(() => AchievementProtocolMapper.MapProgressSummaries(
                [
                    new CPlayer_GetAchievementsProgress_Response.AchievementProgress
                    {
                        appid = 0,
                        unlocked = 0,
                        total = 1
                    }
                ])))
            .Should().Throw<InvalidDataException>();
        ((Action)(() => AchievementProtocolMapper.MapProgressSummaries(
                [
                    new CPlayer_GetAchievementsProgress_Response.AchievementProgress
                    {
                        appid = 1,
                        unlocked = 0,
                        total = uint.MaxValue
                    }
                ])))
            .Should().Throw<InvalidDataException>();
        ((Action)(() => AchievementProtocolMapper.MapProgressSummaries(
                [
                    new CPlayer_GetAchievementsProgress_Response.AchievementProgress
                    {
                        appid = 1,
                        unlocked = uint.MaxValue,
                        total = uint.MaxValue
                    }
                ])))
            .Should().Throw<InvalidDataException>();
        ((Action)(() => AchievementProtocolMapper.MapProgressSummaries(
                [
                    new CPlayer_GetAchievementsProgress_Response.AchievementProgress
                    {
                        appid = 1,
                        unlocked = 0,
                        total = 4,
                        percentage = float.NaN
                    }
                ])))
            .Should().Throw<InvalidDataException>();
        ((Action)(() => AchievementProtocolMapper.MapProgressSummaries(
                [
                    new CPlayer_GetAchievementsProgress_Response.AchievementProgress
                    {
                        appid = 1,
                        unlocked = 0,
                        total = 4,
                        percentage = 150f
                    }
                ])))
            .Should().Throw<InvalidDataException>();
        ((Action)(() => AchievementProtocolMapper.MapProgressSummaries(
                [
                    new CPlayer_GetAchievementsProgress_Response.AchievementProgress
                    {
                        appid = 1,
                        unlocked = 0,
                        total = 4,
                        percentage = -1f
                    }
                ])))
            .Should().Throw<InvalidDataException>();
    }

    [Test]
    public void ProgressSummaries_EmptyInputProducesEmptyResult()
    {
        AchievementProtocolMapper.MapProgressSummaries([]).Should().BeEmpty();
    }

    [Test]
    public void MapUnlocks_OrdinaryFixturePreservesDecoderSemantics()
    {
        var decoded = SanitizedAchievementUserStatsFixtureDecoder.Decode(LoadFixture("ordinary.json"));

        var snapshot = AchievementProtocolMapper.MapUnlocks(100000001UL, 1, 3, decoded.Entries);

        snapshot.SteamId.Should().Be(100000001UL);
        snapshot.AppId.Should().Be(1u);
        snapshot.SessionGeneration.Should().Be(3);
        snapshot.Entries.Should().HaveCount(2);

        var alpha = snapshot.Entries[0];
        alpha.InternalKey.Should().Be(11u);
        alpha.InternalName.Should().Be("ACH_SYNTH_ALPHA");
        alpha.IsUnlocked.Should().BeTrue();
        alpha.UnlockTimeUtc.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1700000000));

        var beta = snapshot.Entries[1];
        beta.InternalKey.Should().Be(12u);
        beta.InternalName.Should().Be("ACH_SYNTH_BETA");
        beta.IsUnlocked.Should().BeFalse();
        beta.UnlockTimeUtc.Should().BeNull();
    }

    [Test]
    public void MapUnlocks_BoundaryFixtureKeepsUnknownStates()
    {
        var decoded = SanitizedAchievementUserStatsFixtureDecoder.Decode(
            LoadFixture("hidden-group-boundary.json"));

        var snapshot = AchievementProtocolMapper.MapUnlocks(100000001UL, 1, 3, decoded.Entries);

        snapshot.Entries.Should().HaveCount(3);
        var byName = snapshot.Entries.ToDictionary(
            entry => entry.InternalName, StringComparer.Ordinal);
        byName["ACH_SYNTH_SECRET"].IsUnlocked.Should().BeTrue();
        byName["ACH_SYNTH_SECRET"].UnlockTimeUtc.Should()
            .Be(DateTimeOffset.FromUnixTimeSeconds(1700000001));
        byName["ACH_SYNTH_OUT_OF_RANGE"].IsUnlocked.Should().BeNull();
        byName["ACH_SYNTH_OUT_OF_RANGE"].UnlockTimeUtc.Should().BeNull();
        byName["ACH_SYNTH_MISSING_BLOCK"].IsUnlocked.Should().BeNull();
        byName["ACH_SYNTH_MISSING_BLOCK"].UnlockTimeUtc.Should().BeNull();
    }

    private static string LoadFixture(string name)
    {
        var resourceName = $"SteamStat.Core.Tests.Fixtures.Achievements.{name}";
        using var stream = typeof(AchievementProgressMapperTests).Assembly.GetManifestResourceStream(resourceName);
        stream.Should().NotBeNull($"embedded fixture '{resourceName}' must exist");
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }
}
