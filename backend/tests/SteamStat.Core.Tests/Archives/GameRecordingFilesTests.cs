using FluentAssertions;
using NUnit.Framework;
using ProtoBuf;
using SteamStat.Core.Steam.Gateway.Internal;
using SteamStat.Core.Archives.Recordings;

namespace SteamStat.Core.Tests.Archives;

[TestFixture]
public sealed class GameRecordingFilesTests
{
    private static long Utc(int year, int month, int day, int hour, int minute, int second)
        => new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero).ToUnixTimeSeconds();

    [TestCase("timeline_73020260327_050839", 730)]
    [TestCase("timeline_144579020261015_210113.json", 1445790)]
    [TestCase("timeline_730_20260327_050839", 730)]
    public void TimelineNamesHaveNoSeparatorBeforeTheDate(string name, int appId)
    {
        GameRecordingFiles.TryParseTimelineId(name, out var parsedApp, out var startedAt).Should().BeTrue();
        parsedApp.Should().Be(appId);
        startedAt.Should().BeGreaterThan(0);
    }

    [Test]
    public void TheTimeInANameIsUtc()
    {
        GameRecordingFiles.TryParseTimelineId("timeline_73020260327_050839", out _, out var startedAt);
        startedAt.Should().Be(Utc(2026, 3, 27, 5, 8, 39));
    }

    [TestCase("timeline_20261015_210113")]
    [TestCase("timeline_73020261315_210113")]
    [TestCase("other.json")]
    public void BadTimelineNamesAreRejected(string name) => GameRecordingFiles.TryParseTimelineId(name, out _, out _).Should().BeFalse();

    [TestCase("bg_730_20260327_050839", RecordingKinds.Background)]
    [TestCase("fg_730_20260327_050839", RecordingKinds.Background)]
    [TestCase("clip_730_20260327_050839", RecordingKinds.Clip)]
    public void FolderNamesGiveTheKind(string name, string kind)
    {
        GameRecordingFiles.TryParseFolder(name, out var parsed, out var appId, out var startedAt).Should().BeTrue();
        parsed.Should().Be(kind);
        appId.Should().Be(730);
        startedAt.Should().Be(Utc(2026, 3, 27, 5, 8, 39));
    }

    [Test]
    public void TagsAreTurnedAroundIntoTimelines()
    {
        // 和本机真实文件的形状一样：只有 tags，没有 timelines
        var file = new CGameRecordingFile();
        var tag = new RecordingTag { game_id = 730, tag = new TimelineTag { name = "Dust II", group = "Map", icon = "map_icon_de_dust2", priority = 500 } };
        tag.references.Add(new TagReference { timeline_id = "timeline_73020260309_041149", offset_ms = 113519 });
        tag.references.Add(new TagReference { timeline_id = "timeline_73020260311_045543", offset_ms = 1865424 });
        file.tags.Add(tag);

        var timelines = GameRecordingFiles.ReadIndex(Serialize(file));

        timelines.Should().HaveCount(2);
        timelines[0].AppId.Should().Be(730);
        timelines[0].StartedAt.Should().Be(Utc(2026, 3, 9, 4, 11, 49));
        timelines[0].Markers.Should().ContainSingle().Which.Should().Be(new RecordingMarker("tag", "Dust II", "Map", "map_icon_de_dust2", 113519, null));
    }

    [Test]
    public void TimelineMetadataWinsOverTheName()
    {
        var file = new CGameRecordingFile();
        var timeline = new TimelineMetadata { timeline_id = "timeline_73020260309_041149", game_id = 730, date_recorded = 1773000000, duration_ms = 90000 };
        timeline.recordings.Add(new TimelineRecording { recording_id = "bg_730_20260309_041149", duration_ms = 60000, start_offset_ms = 1000, file_size = 5 });
        timeline.significant_events.Add(new TimelineEvent { marker_title = "Ace", marker_icon = "star", timeline_offset_ms = 500, user_marker = true });
        timeline.phases.Add(Phase("Mode", "Competitive", 2000));
        file.timelines.Add(timeline);

        var result = GameRecordingFiles.ReadIndex(Serialize(file)).Should().ContainSingle().Subject;

        result.StartedAt.Should().Be(1773000000);
        result.DurationMs.Should().Be(90000);
        result.Videos.Should().ContainSingle().Which.Should().Be(new RecordingVideo("bg_730_20260309_041149", 1000, 60000, 5));
        result.Markers.Should().HaveCount(2);
        result.Markers[0].Should().Be(new RecordingMarker("usermarker", "Ace", null, "star", 500, null));
        result.Markers[1].Kind.Should().Be("phase");
        result.Markers[1].Title.Should().Be("Competitive");
        result.Markers[1].Group.Should().Be("Mode");
    }

    [Test]
    public void ANonSteamGameIdFallsBackToTheName()
    {
        var file = new CGameRecordingFile();
        file.timelines.Add(new TimelineMetadata { timeline_id = "timeline_73020260309_041149", game_id = 0x8000000002000000 });

        GameRecordingFiles.ReadIndex(Serialize(file)).Should().ContainSingle().Which.AppId.Should().Be(730);
    }

    [Test]
    public void ReadsAClip()
    {
        var file = new CGameRecordingClipFile { date_recorded = 1773000000, game_id = 620, size_in_bytes = 4096, name = "Nice shot" };
        var timeline = new TimelineMetadata { timeline_id = "timeline_62020260309_041149" };
        timeline.recordings.Add(new TimelineRecording { recording_id = "bg", duration_ms = 30000 });
        file.timelines.Add(timeline);
        file.tags.Add(new RecordingTag { tag = new TimelineTag { name = "Chapter 1", group = "Stage" } });

        var clip = GameRecordingFiles.ReadClip(Serialize(file), "clip_620_20260309_041149");

        clip.Should().NotBeNull();
        clip!.AppId.Should().Be(620);
        clip.Name.Should().Be("Nice shot");
        clip.DurationMs.Should().Be(30000);
        clip.SizeBytes.Should().Be(4096);
        clip.TimelineIds.Should().Equal("timeline_62020260309_041149");
        clip.Markers.Should().ContainSingle().Which.Title.Should().Be("Chapter 1");
    }

    [Test]
    public void AClipFallsBackToItsFolderName()
    {
        var clip = GameRecordingFiles.ReadClip(Serialize(new CGameRecordingClipFile()), "clip_620_20260309_041149");

        clip!.AppId.Should().Be(620);
        clip.RecordedAt.Should().Be(Utc(2026, 3, 9, 4, 11, 49));
    }

    [Test]
    public void TimelineJsonStoresNumbersAsStrings()
    {
        const string Json = """
            {"daterecorded":"1792098073","endtime":"5986786","entries":[
              {"id":"1","time":"3735706","type":"usermarker","title":"Boss"},
              {"id":"2","time":12,"type":"achievement","achievement_name":"Holster"},
              {"id":"3","time":"20","type":"error","title":"#GameRecording_RecordingFailed"},
              {"id":"4","type":"state"},
              {"id":"5","time":"x","type":"state"}
            ]}
            """;

        var (end, markers) = GameRecordingFiles.ReadTimelineJson(Json);

        end.Should().Be(5986786);
        markers.Should().HaveCount(3);
        markers[0].Should().Be(new RecordingMarker("usermarker", "Boss", null, null, 3735706, null));
        markers[1].Title.Should().Be("Holster");
        markers[2].Title.Should().BeEmpty("本地化键不是给人看的");
    }

    [Test]
    public void AnEmptyIndexIsEmpty() => GameRecordingFiles.ReadIndex(new MemoryStream()).Should().BeEmpty();

    private static RecordingPhase Phase(string group, string value, ulong offset)
    {
        var phase = new RecordingPhase { phase_id = "p", background_timeline_offset = offset, duration_ms = 100 };
        phase.attributes.Add(new PhaseAttribute { group = group, value = value, priority = 1 });
        return phase;
    }

    private static MemoryStream Serialize<T>(T value)
    {
        var stream = new MemoryStream();
        Serializer.Serialize(stream, value);
        stream.Position = 0;
        return stream;
    }
}
