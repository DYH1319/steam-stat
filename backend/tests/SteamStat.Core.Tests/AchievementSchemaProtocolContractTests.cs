using FluentAssertions;
using ProtoBuf;
using SteamKit2.Internal;
using SteamStat.Core.Steam.Gateway.Internal;

namespace SteamStat.Core.Tests;

[TestFixture]
public sealed class AchievementSchemaProtocolContractTests
{
    [Test]
    public void ServiceMethod_MatchesUpstreamName()
    {
        AchievementSchemaProtocol.ServiceMethod.Should().Be("Player.GetGameAchievements#1");
    }

    [Test]
    public void ProtocolTags_MatchUpstreamContract()
    {
        AssertTags(
            typeof(AchievementSchemaRequest),
            ("appid", 1), ("language", 2), ("hash_only", 3));
        AssertTags(
            typeof(AchievementSchemaResponse),
            ("achievements", 1), ("schema_version", 2), ("groups", 3), ("schema_hash", 4));
        AssertTags(
            typeof(AchievementSchemaResponse.Achievement),
            ("internal_name", 1),
            ("localized_name", 2),
            ("localized_desc", 3),
            ("icon", 4),
            ("icon_gray", 5),
            ("hidden", 6),
            ("player_percent_unlocked", 7),
            ("internal_key", 8),
            ("min_progress_int", 9),
            ("max_progress_int", 10),
            ("groupid", 11),
            ("archived", 12),
            ("progress_type", 13),
            ("min_progress_float", 14),
            ("max_progress_float", 15));
        AssertTags(
            typeof(AchievementSchemaResponse.Group),
            ("groupid", 1),
            ("localized_name", 2),
            ("dlcappid", 3),
            ("archived", 4),
            ("developeronly", 5),
            ("order", 6),
            ("ispublic", 7),
            ("total_achievements", 8),
            ("completion_achievements", 9));
    }

    [Test]
    public void HandWrittenRequest_SerializesIntoSteamKitRequest()
    {
        var request = new AchievementSchemaRequest
        {
            appid = 1,
            language = "english",
            hash_only = true
        };

        using var stream = new MemoryStream();
        Serializer.Serialize(stream, request);
        stream.Position = 0;
        var decoded = Serializer.Deserialize<CPlayer_GetGameAchievements_Request>(stream);

        decoded.appid.Should().Be(1u);
        decoded.language.Should().Be("english");
    }

    [Test]
    public void SteamKitResponse_DeserializesIntoHandWrittenResponse()
    {
        var generated = new CPlayer_GetGameAchievements_Response();
        generated.achievements.Add(new CPlayer_GetGameAchievements_Response.Achievement
        {
            internal_name = "ACH_SYNTH",
            localized_name = "Synthetic",
            localized_desc = "Synthetic description",
            icon = "synthetic_icon.jpg",
            icon_gray = "synthetic_icon_gray.jpg",
            hidden = true,
            player_percent_unlocked = "12.5"
        });

        using var stream = new MemoryStream();
        Serializer.Serialize(stream, generated);
        stream.Position = 0;
        var decoded = Serializer.Deserialize<AchievementSchemaResponse>(stream);

        decoded.schema_version.Should().BeNull();
        decoded.schema_hash.Should().BeNull();
        decoded.groups.Should().BeEmpty();
        var achievement = decoded.achievements.Should().ContainSingle().Subject;
        achievement.internal_name.Should().Be("ACH_SYNTH");
        achievement.localized_name.Should().Be("Synthetic");
        achievement.localized_desc.Should().Be("Synthetic description");
        achievement.icon.Should().Be("synthetic_icon.jpg");
        achievement.icon_gray.Should().Be("synthetic_icon_gray.jpg");
        achievement.hidden.Should().BeTrue();
        achievement.player_percent_unlocked.Should().Be("12.5");
        achievement.internal_key.Should().BeNull();
    }

    [Test]
    public void HandWrittenResponse_RoundTripsExtendedFields()
    {
        var response = new AchievementSchemaResponse
        {
            schema_version = 7,
            schema_hash = 305419896u
        };
        response.achievements.Add(new AchievementSchemaResponse.Achievement
        {
            internal_name = "ACH_SYNTH",
            internal_key = 11,
            progress_type = 7,
            groupid = 1,
            min_progress_int = 0,
            max_progress_int = 10
        });
        response.groups.Add(new AchievementSchemaResponse.Group
        {
            groupid = 1,
            localized_name = "Synthetic Group",
            dlcappid = 424242,
            order = 1,
            ispublic = true
        });

        using var stream = new MemoryStream();
        Serializer.Serialize(stream, response);
        stream.Position = 0;
        var decoded = Serializer.Deserialize<AchievementSchemaResponse>(stream);

        decoded.schema_version.Should().Be(7);
        decoded.schema_hash.Should().Be(305419896u);
        var achievement = decoded.achievements.Should().ContainSingle().Subject;
        achievement.internal_key.Should().Be(11u);
        achievement.progress_type.Should().Be(7);
        achievement.groupid.Should().Be(1u);
        achievement.min_progress_int.Should().Be(0);
        achievement.max_progress_int.Should().Be(10);
        var group = decoded.groups.Should().ContainSingle().Subject;
        group.groupid.Should().Be(1u);
        group.dlcappid.Should().Be(424242u);
        group.ispublic.Should().BeTrue();
    }

    [Test]
    public void SteamKitResponse_ReshapesIntoHandWrittenResponse_PreservingExtendedFields()
    {
        var response = new AchievementSchemaResponse
        {
            schema_version = 7,
            schema_hash = 305419896u
        };
        response.achievements.Add(new AchievementSchemaResponse.Achievement
        {
            internal_name = "ACH_SYNTH",
            internal_key = 11,
            progress_type = 7,
            groupid = 1,
            min_progress_int = 0,
            max_progress_int = 10
        });
        response.groups.Add(new AchievementSchemaResponse.Group
        {
            groupid = 1,
            localized_name = "Synthetic Group",
            dlcappid = 424242,
            order = 1,
            ispublic = true
        });

        using var stream = new MemoryStream();
        Serializer.Serialize(stream, response);
        stream.Position = 0;
        var steamKitBody = Serializer.Deserialize<CPlayer_GetGameAchievements_Response>(stream);

        var reshaped = ProtobufReshape.To<AchievementSchemaResponse>(steamKitBody);

        reshaped.schema_version.Should().Be(7);
        reshaped.schema_hash.Should().Be(305419896u);
        var achievement = reshaped.achievements.Should().ContainSingle().Subject;
        achievement.internal_name.Should().Be("ACH_SYNTH");
        achievement.internal_key.Should().Be(11u);
        achievement.progress_type.Should().Be(7);
        achievement.groupid.Should().Be(1u);
        achievement.min_progress_int.Should().Be(0);
        achievement.max_progress_int.Should().Be(10);
        var group = reshaped.groups.Should().ContainSingle().Subject;
        group.groupid.Should().Be(1u);
        group.localized_name.Should().Be("Synthetic Group");
        group.dlcappid.Should().Be(424242u);
        group.order.Should().Be(1u);
        group.ispublic.Should().BeTrue();
    }

    [Test]
    public void HashOnlyRequest_IsCarriedAsExtensionField3()
    {
        var request = new CPlayer_GetGameAchievements_Request
        {
            appid = 1,
            language = "english"
        };
        Extensible.AppendValue(request, 3, true);

        using var stream = new MemoryStream();
        Serializer.Serialize(stream, request);
        stream.Position = 0;
        var decoded = Serializer.Deserialize<AchievementSchemaRequest>(stream);

        decoded.appid.Should().Be(1u);
        decoded.language.Should().Be("english");
        decoded.hash_only.Should().BeTrue();

        var plain = new CPlayer_GetGameAchievements_Request
        {
            appid = 1,
            language = "english"
        };
        using var plainStream = new MemoryStream();
        Serializer.Serialize(plainStream, plain);
        plainStream.Position = 0;
        Serializer.Deserialize<AchievementSchemaRequest>(plainStream).hash_only.Should().BeFalse();
    }

    private static void AssertTags(Type type, params (string Name, int Tag)[] expected)
    {
        var actual = type.GetProperties()
            .Select(property => (property.Name,
                Tag: property.GetCustomAttributes(typeof(ProtoMemberAttribute), false)
                    .Cast<ProtoMemberAttribute>().Single().Tag))
            .OrderBy(item => item.Tag)
            .ToArray();
        actual.Should().BeEquivalentTo(
            expected.OrderBy(item => item.Tag),
            options => options.WithStrictOrdering(),
            $"{type.Name} must match the upstream tag table");
    }
}
