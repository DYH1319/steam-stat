using ElectronNet.Features.History;
using FluentAssertions;

namespace ElectronNet.Tests.Features.History;

[TestFixture]
public sealed class LocalConfigPlaytimeReaderTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "steam-stat-history-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Test]
    public void Read_ParsesPlaytimeWithCapitalizedAppsKey()
    {
        var path = WriteFile("localconfig.vdf",
            """
            "UserLocalConfigStore"
            {
            	"Software"
            	{
            		"valve"
            		{
            			"Steam"
            			{
            				"Apps"
            				{
            					"570"
            					{
            						"Playtime"		"1234"
            						"LastPlayed"		"1700000000"
            					}
            					"730"
            					{
            						"Playtime"		"0"
            					}
            					"440"
            					{
            						"Playtime"		"60"
            						"LastPlayed"		"0"
            					}
            					"notanapp"
            					{
            						"Playtime"		"99"
            					}
            				}
            			}
            		}
            	}
            }
            """);

        var result = LocalConfigPlaytimeReader.Read(path, "76561198012345678", 1700000000);

        result.Should().HaveCount(2);
        result[0].SteamId.Should().Be("76561198012345678");
        result[0].AppId.Should().Be(570);
        result[0].PlaytimeMinutes.Should().Be(1234);
        result[0].LastPlayed.Should().Be(1700000000);
        result[1].AppId.Should().Be(440);
        result[1].LastPlayed.Should().BeNull();
    }

    [Test]
    public void Read_ReturnsEmptyWhenAppsMissing()
    {
        var path = WriteFile("localconfig.vdf",
            """
            "UserLocalConfigStore"
            {
            	"Software"		{}
            }
            """);

        LocalConfigPlaytimeReader.Read(path, "s", 1700000000).Should().BeEmpty();
    }

    [Test]
    public void EnumerateUserConfigs_FindsValidAccountDirs()
    {
        WriteFile(Path.Combine("userdata", "7654321", "config", "localconfig.vdf"), "\"x\"{}");
        WriteFile(Path.Combine("userdata", "notanumber", "config", "localconfig.vdf"), "\"x\"{}");
        WriteFile(Path.Combine("userdata", "0", "config", "localconfig.vdf"), "\"x\"{}");
        WriteFile(Path.Combine("userdata", "999", "config", "other.vdf"), "\"x\"{}");

        var result = LocalConfigPlaytimeReader.EnumerateUserConfigs(_tempDir).ToList();

        result.Should().ContainSingle();
        result[0].SteamId.Should().Be((76561197960265728UL + 7654321UL).ToString());
        result[0].Path.Should().EndWith("localconfig.vdf");
    }
}
