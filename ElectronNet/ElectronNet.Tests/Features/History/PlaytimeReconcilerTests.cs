using ElectronNet.Features.History;
using ElectronNet.Models;
using FluentAssertions;
using ValveKeyValue;

namespace ElectronNet.Tests.Features.History;

[TestFixture]
public sealed class PlaytimeReconcilerTests
{
    private const string SteamId = "76561198000000001";

    [Test]
    public void Merge_ExtendsPlateausOpensNewSegmentsAndIgnoresStaleValues()
    {
        var latest = new Dictionary<(string SteamId, int AppId), PlaytimeSnapshot>();

        var first = PlaytimeSnapshotMerger.Merge(latest, [Observation(100, 1_000)]);
        var same = PlaytimeSnapshotMerger.Merge(latest, [Observation(100, 2_000)]);
        var stale = PlaytimeSnapshotMerger.Merge(latest, [Observation(90, 2_500, "cm")]);
        var grown = PlaytimeSnapshotMerger.Merge(latest, [Observation(160, 3_000)]);

        first.Should().ContainSingle();
        same.Should().BeEmpty();
        stale.Should().BeEmpty();
        first[0].LastObservedAt.Should().Be(2_000);
        grown.Should().ContainSingle().Which.Should().Match<PlaytimeSnapshot>(s =>
            s.PlaytimeMinutes == 160 && s.FirstObservedAt == 3_000);
    }

    [Test]
    public void Reconcile_ComputesBaselineAndFlagsUntrackedPlay()
    {
        var snapshots = new List<PlaytimeSnapshot>
        {
            Snapshot(600, 10_000, 20_000),
            Snapshot(660, 30_000, 30_000),
            Snapshot(900, 50_000, 60_000)
        };
        var sessions = new List<LocalSessionSpan>
        {
            new(5_000, 8_600, 3_600),
            new(25_000, 28_600, 3_600)
        };

        var result = PlaytimeReconciler.Reconcile(snapshots, sessions);

        result.BaselineSeconds.Should().Be(600 * 60 - 3_600);
        result.OfficialDeltaSeconds.Should().Be(300 * 60);
        result.LocalTrackedSeconds.Should().Be(3_600);
        result.UntrackedSeconds.Should().Be(300 * 60 - 3_600);
        result.Status.Should().Be(PlaytimeReconciler.Untracked);
        result.Discrepancies.Should().ContainSingle().Which.Should().Be(
            new PlaytimeDiscrepancy(30_000, 50_000, 240 * 60, 0));
    }

    [Test]
    public void Reconcile_MatchesWithinTolerance()
    {
        var result = PlaytimeReconciler.Reconcile(
            [Snapshot(60, 1_000, 1_000), Snapshot(120, 9_000, 9_000)],
            [new LocalSessionSpan(2_000, 5_400, 3_400)]);

        result.Status.Should().Be(PlaytimeReconciler.Matched);
        result.Discrepancies.Should().BeEmpty();
    }

    [Test]
    public void LocalConfigReader_ReadsPlaytimeCaseInsensitively()
    {
        const string vdf = """
            "UserLocalConfigStore"
            {
              "Software"
              {
                "Valve"
                {
                  "Steam"
                  {
                    "Apps"
                    {
                      "570" { "LastPlayed" "1767312000" "Playtime" "1234" }
                      "730" { "Playtime" "0" }
                      "abc" { "Playtime" "5" }
                    }
                  }
                }
              }
            }
            """;
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(vdf));
        var root = KVSerializer.Create(KVSerializationFormat.KeyValues1Text).Deserialize(stream);

        var observations = LocalConfigPlaytimeReader.Parse(root, SteamId, 42).ToList();

        observations.Should().ContainSingle().Which.Should().Be(
            new PlaytimeObservation(SteamId, 570, 1234, 1767312000, "localconfig", 42));
    }

    private static PlaytimeObservation Observation(int minutes, int at, string source = "localconfig")
        => new(SteamId, 570, minutes, null, source, at);

    private static PlaytimeSnapshot Snapshot(int minutes, int first, int last) => new()
    {
        SteamId = SteamId,
        AppId = 570,
        PlaytimeMinutes = minutes,
        FirstObservedAt = first,
        LastObservedAt = last
    };
}
