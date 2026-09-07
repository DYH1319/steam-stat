using System.Text.Json;
using System.Text.Json.Serialization;
using ElectronNet.Hosting;
using FluentAssertions;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Events;

namespace ElectronNet.Tests.Services;

[TestFixture]
public sealed class EventJsonCompatibilityTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [Test]
    public void SteamLoginEvent_KeepsCamelCaseWireShape()
    {
        var dto = ElectronIpcEventForwarder.ToDto(
            new SteamLoginProgressChanged("success", new SteamLoginProgressData(AccountName: "alice")));

        JsonSerializer.Serialize(dto, JsonOptions).Should().Be(
            "{\"type\":\"success\",\"data\":{\"accountName\":\"alice\"}}");
    }

    [Test]
    public void SteamLoginQrEvent_DoesNotExposeChallengeUrl()
    {
        var dto = ElectronIpcEventForwarder.ToDto(
            new SteamLoginProgressChanged("qrCode", new SteamLoginProgressData(QrImageBase64: "data:image/png;base64,AA==")));

        var json = JsonSerializer.Serialize(dto, JsonOptions);

        json.Should().Be("{\"type\":\"qrCode\",\"data\":{\"qrImageBase64\":\"data:image/png;base64,AA==\"}}");
        json.Should().NotContain("challengeUrl");
    }

    [Test]
    public void FriendsEvent_KeepsCamelCaseWireShape()
    {
        var friend = new SteamFriendSnapshot(
            "76561198000000001", "Bob", 1, 2, 3, "Game", "10", "hash", 11, 12, "Lobby", 42);
        var data = new SteamFriendsSnapshot("alice", friend, [friend], 123);
        var dto = ElectronIpcEventForwarder.ToDto(new FriendsChanged("alice", data));

        JsonSerializer.Serialize(dto, JsonOptions).Should().Be(
            "{\"accountName\":\"alice\",\"data\":{\"accountName\":\"alice\",\"currentUser\":{\"steamId\":\"76561198000000001\",\"personaName\":\"Bob\",\"personaState\":1,\"personaStateFlags\":2,\"relationship\":3,\"gameName\":\"Game\",\"gameId\":\"10\",\"avatarHash\":\"hash\",\"lastLogOff\":11,\"lastLogOn\":12,\"richPresence\":\"Lobby\",\"level\":42},\"friends\":[{\"steamId\":\"76561198000000001\",\"personaName\":\"Bob\",\"personaState\":1,\"personaStateFlags\":2,\"relationship\":3,\"gameName\":\"Game\",\"gameId\":\"10\",\"avatarHash\":\"hash\",\"lastLogOff\":11,\"lastLogOn\":12,\"richPresence\":\"Lobby\",\"level\":42}],\"lastUpdateTime\":123}}");
    }

    [Test]
    public void UpdaterEvent_KeepsCamelCaseWireShape()
    {
        var dto = new UpdaterStateChanged(new UpdaterEventDto
        {
            UpdaterEvent = "download-progress",
            Data = new UpdaterDetailsEventPayload(new UpdaterEventDataDto { Percent = 50.5 })
        }).Event;

        JsonSerializer.Serialize(dto, JsonOptions).Should().Be(
            "{\"updaterEvent\":\"download-progress\",\"data\":{\"percent\":50.5}}");
    }
}
