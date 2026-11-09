using FluentAssertions;
using SteamKit2.Internal;
using SteamStat.Core.Features.Achievements;
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
            new AchievementsProgressResponse.Item
            {
                appid = 1,
                unlocked = 3,
                total = 7,
                percentage = 42.5f
            },
            new AchievementsProgressResponse.Item
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
                    new AchievementsProgressResponse.Item
                    {
                        appid = 1,
                        unlocked = 0,
                        total = 1
                    },
                    new AchievementsProgressResponse.Item
                    {
                        appid = 1,
                        unlocked = 0,
                        total = 0
                    }
                ])))
            .Should().Throw<InvalidDataException>();
    }

    [Test]
    public void ProgressSummaries_MoreUnlockedThanTotalDoesNotFailTheBatch()
    {
        // 退款的 DLC、已归档成就都可能这样，一款游戏不能让同批 100 款一起失败
        var snapshots = AchievementProtocolMapper.MapProgressSummaries(
        [
            new AchievementsProgressResponse.Item { appid = 1, unlocked = 5, total = 2, percentage = 250f },
            new AchievementsProgressResponse.Item { appid = 2, unlocked = 1, total = 2, percentage = 50f }
        ]);

        snapshots[0].Should().Be(new SteamAchievementAppProgressSnapshot(1, 2, 2, 100));
        snapshots[1].Unlocked.Should().Be(1);
    }

    [Test]
    public void ProgressSummaries_ReadSetFieldsThatSteamKitDoesNotKnow()
    {
        using var wire = new MemoryStream();
        var sent = new AchievementsProgressResponse();
        sent.achievement_progress.Add(new AchievementsProgressResponse.Item
        {
            appid = 1158310,
            unlocked = 3,
            total = 56,
            percentage = 5.3f,
            groups_completed_base = 1,
            groups_total_base = 2,
            groups_completed_dlc = 0,
            groups_total_dlc = 10,
            base_game_total = 56
        });
        ProtoBuf.Serializer.Serialize(wire, sent);
        wire.Position = 0;

        // 走和线上一样的路：先解析成 SteamKit 的类型，未知字段留在扩展数据里
        var steamKit = ProtoBuf.Serializer.Deserialize<CPlayer_GetAchievementsProgress_Response>(wire);
        var body = ProtobufReshape.To<AchievementsProgressResponse>(steamKit);
        var snapshot = AchievementProtocolMapper.MapProgressSummaries(body.achievement_progress).Single();

        snapshot.BaseGameTotal.Should().Be(56);
        snapshot.GroupsCompletedBase.Should().Be(1);
        snapshot.GroupsTotalBase.Should().Be(2);
        snapshot.GroupsCompletedDlc.Should().Be(0);
        snapshot.GroupsTotalDlc.Should().Be(10);
    }

    [Test]
    public void ProgressSummaries_RejectOutOfRangeItems()
    {
        ((Action)(() => AchievementProtocolMapper.MapProgressSummaries(
                [
                    new AchievementsProgressResponse.Item
                    {
                        appid = 0,
                        unlocked = 0,
                        total = 1
                    }
                ])))
            .Should().Throw<InvalidDataException>();
        ((Action)(() => AchievementProtocolMapper.MapProgressSummaries(
                [
                    new AchievementsProgressResponse.Item
                    {
                        appid = 1,
                        unlocked = 0,
                        total = uint.MaxValue
                    }
                ])))
            .Should().Throw<InvalidDataException>();
        ((Action)(() => AchievementProtocolMapper.MapProgressSummaries(
                [
                    new AchievementsProgressResponse.Item
                    {
                        appid = 1,
                        unlocked = uint.MaxValue,
                        total = uint.MaxValue
                    }
                ])))
            .Should().Throw<InvalidDataException>();
        ((Action)(() => AchievementProtocolMapper.MapProgressSummaries(
                [
                    new AchievementsProgressResponse.Item
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
                    new AchievementsProgressResponse.Item
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
                    new AchievementsProgressResponse.Item
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
