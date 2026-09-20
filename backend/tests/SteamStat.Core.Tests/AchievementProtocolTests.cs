using System.Text;
using System.Text.Json;
using FluentAssertions;
using SteamStat.Core.Steam.Gateway.Internal;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class AchievementProtocolTests
{
    [Test]
    public void OrdinaryFixture_MapsUnlockedAndLockedUtcSemantics()
    {
        var decoded = SanitizedAchievementUserStatsFixtureDecoder.Decode(LoadFixture("ordinary.json"));

        decoded.SchemaVersion.Should().Be(7);
        decoded.SchemaHash.Should().Be(305419896u);
        decoded.Entries.Should().HaveCount(2);

        var unlocked = decoded.Entries[0];
        unlocked.InternalName.Should().Be("ACH_SYNTH_ALPHA");
        unlocked.InternalKey.Should().Be(11u);
        unlocked.Coordinate.Should().Be(new AchievementProtocolCoordinate(4u, 0u));
        unlocked.IsUnlocked.Should().BeTrue();
        unlocked.UnlockTimeUtc.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1700000000));

        var locked = decoded.Entries[1];
        locked.InternalName.Should().Be("ACH_SYNTH_BETA");
        locked.Coordinate.Should().Be(new AchievementProtocolCoordinate(4u, 1u));
        locked.IsUnlocked.Should().BeFalse();
        locked.UnlockTimeUtc.Should().BeNull();

        decoded.UnmatchedUnlockedCoordinates.Should().BeEmpty();
    }

    [Test]
    public void EmptyFixture_ProducesEmptyResult()
    {
        var decoded = SanitizedAchievementUserStatsFixtureDecoder.Decode(LoadFixture("empty.json"));

        decoded.Entries.Should().BeEmpty();
        decoded.UnmatchedUnlockedCoordinates.Should().BeEmpty();
    }

    [Test]
    public void HiddenGroupBoundaryFixture_PreservesMetadataAndMarksUnknownAndUnmatched()
    {
        var decoded = SanitizedAchievementUserStatsFixtureDecoder.Decode(LoadFixture("hidden-group-boundary.json"));

        decoded.Entries.Should().HaveCount(3);

        var secret = decoded.Entries[0];
        secret.InternalName.Should().Be("ACH_SYNTH_SECRET");
        secret.Hidden.Should().BeTrue();
        secret.GroupId.Should().Be(5u);
        secret.IsUnlocked.Should().BeTrue();
        secret.UnlockTimeUtc.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1700000001));

        var outOfRange = decoded.Entries[1];
        outOfRange.InternalName.Should().Be("ACH_SYNTH_OUT_OF_RANGE");
        outOfRange.IsUnlocked.Should().BeNull();
        outOfRange.UnlockTimeUtc.Should().BeNull();

        var missingBlock = decoded.Entries[2];
        missingBlock.InternalName.Should().Be("ACH_SYNTH_MISSING_BLOCK");
        missingBlock.GroupId.Should().Be(5u);
        missingBlock.IsUnlocked.Should().BeNull();
        missingBlock.UnlockTimeUtc.Should().BeNull();

        decoded.UnmatchedUnlockedCoordinates.Should().Equal([new AchievementProtocolCoordinate(77u, 1u)]);
    }

    [Test]
    public void MalformedFixturePayloads_AreRejected()
    {
        var duplicateCoordinate = Payload(
            """{"statId":4,"bit":0,"internalKey":1,"internalName":"ACH_A","hidden":false,"groupId":null}""",
            """{"statId":4,"bit":0,"internalKey":2,"internalName":"ACH_B","hidden":false,"groupId":null}""");
        var duplicateName = Payload(
            """{"statId":4,"bit":0,"internalKey":1,"internalName":"ACH_A","hidden":false,"groupId":null}""",
            """{"statId":4,"bit":1,"internalKey":2,"internalName":"ACH_A","hidden":false,"groupId":null}""");
        var emptyName = Payload(
            """{"statId":4,"bit":0,"internalKey":1,"internalName":"","hidden":false,"groupId":null}""");
        var duplicateBlock =
            """{"schema":{"schemaVersion":1,"schemaHash":0,"definitions":[],"groups":[]},"achievementBlocks":[{"achievementId":4,"unlockTime":[1]},{"achievementId":4,"unlockTime":[0]}]}""";

        ((Action)(() => SanitizedAchievementUserStatsFixtureDecoder.Decode(null!)))
            .Should().Throw<InvalidDataException>();
        ((Action)(() => SanitizedAchievementUserStatsFixtureDecoder.Decode("{ not json")))
            .Should().Throw<InvalidDataException>();
        ((Action)(() => SanitizedAchievementUserStatsFixtureDecoder.Decode("null")))
            .Should().Throw<InvalidDataException>();
        ((Action)(() => SanitizedAchievementUserStatsFixtureDecoder.Decode("{}")))
            .Should().Throw<InvalidDataException>();
        ((Action)(() => SanitizedAchievementUserStatsFixtureDecoder.Decode(duplicateCoordinate)))
            .Should().Throw<InvalidDataException>();
        ((Action)(() => SanitizedAchievementUserStatsFixtureDecoder.Decode(duplicateName)))
            .Should().Throw<InvalidDataException>();
        ((Action)(() => SanitizedAchievementUserStatsFixtureDecoder.Decode(emptyName)))
            .Should().Throw<InvalidDataException>();
        ((Action)(() => SanitizedAchievementUserStatsFixtureDecoder.Decode(duplicateBlock)))
            .Should().Throw<InvalidDataException>();
    }

    [Test]
    public void Representative1328AchievementSchemaPayload_IsBelowCurrentCacheLimit()
    {
        var achievements = Enumerable.Range(0, 1328).Select(_ => new
        {
            internal_key = 1,
            stat_id = 1,
            bit = 0,
            internal_name = "ACH_SYNTHETIC_BENCHMARK",
            localized_name = "Synthetic Benchmark Achievement",
            localized_desc = "Synthetic benchmark description for payload sizing.",
            localized_name_schinese = "\u5408\u6210\u57fa\u51c6\u6210\u5c31",
            localized_desc_schinese = "\u7528\u4e8e\u8d1f\u8f7d\u5927\u5c0f\u4f30\u8ba1\u7684\u5408\u6210\u6210\u5c31\u63cf\u8ff0\u3002",
            icon = "synthetic_icon.jpg",
            icon_gray = "synthetic_icon_gray.jpg",
            hidden = false,
            groupid = 1,
            archived = false,
            progress_type = 0,
            min_progress = 0,
            max_progress = 0,
            player_percent_unlocked = "12.5"
        }).ToArray();
        var groups = Enumerable.Range(1, 42).Select(index => new
        {
            groupid = index,
            localized_name = "Synthetic Benchmark Group",
            dlc_appid = 0,
            archived = false,
            developer_only = false,
            is_public = true,
            order = index
        }).ToArray();
        var payload = new
        {
            appid = 0,
            language = "english",
            schema_version = 1,
            schema_hash = 305419896,
            achievements,
            groups
        };

        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetByteCount(json);
        TestContext.Progress.WriteLine($"M0 representative schema UTF-8 bytes: {bytes}");
        bytes.Should().BeLessThan(1024 * 1024);
    }

    private static string LoadFixture(string name)
    {
        var resourceName = $"SteamStat.Core.Tests.Fixtures.Achievements.{name}";
        using var stream = typeof(AchievementProtocolTests).Assembly.GetManifestResourceStream(resourceName);
        stream.Should().NotBeNull($"embedded fixture '{resourceName}' must exist");
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }

    private static string Payload(params string[] definitions) =>
        $$"""{"schema":{"schemaVersion":1,"schemaHash":0,"definitions":[{{string.Join(",", definitions)}}],"groups":[]},"achievementBlocks":[]}""";
}
