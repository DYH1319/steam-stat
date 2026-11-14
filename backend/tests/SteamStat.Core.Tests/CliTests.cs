using System.Text.Json.Nodes;
using FluentAssertions;
using SteamStat.Cli;

namespace SteamStat.Core.Tests;

[TestFixture]
public class CliTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "steam-stat-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Test]
    public void Discovery_ReadsTheStateFileOnlyWhileTheApiIsRunning()
    {
        var file = Path.Combine(_directory, "local-api.json");
        File.WriteAllText(file, """{"token":"ss_abc","port":47615,"running":false}""");
        Discovery.Read(file).Should().BeNull();

        File.WriteAllText(file, """{"token":"ss_abc","port":47615,"running":true}""");
        var endpoint = Discovery.Read(file)!;
        endpoint.BaseUri.Should().Be(new Uri("http://127.0.0.1:47615"));
        endpoint.Token.Should().Be("ss_abc");
    }

    [Test]
    public void Discovery_PrefersExplicitEnvironmentAndDataDirectory()
    {
        var env = new Dictionary<string, string> { ["STEAMSTAT_URL"] = "http://127.0.0.1:5000", ["STEAMSTAT_TOKEN"] = "ss_x" };
        Discovery.Find(null, name => env.GetValueOrDefault(name), _directory)!.Token.Should().Be("ss_x");

        File.WriteAllText(Path.Combine(_directory, "local-api.json"), """{"token":"ss_dir","port":1234,"running":true}""");
        Discovery.Find(_directory, _ => null, "/nowhere")!.Token.Should().Be("ss_dir");
        Discovery.Candidates(_directory, _ => null, "/nowhere").Should().Equal(_directory);
    }

    [Test]
    public void Discovery_FindsPortableData()
    {
        var data = Directory.CreateDirectory(Path.Combine(_directory, "data")).FullName;
        File.WriteAllText(Path.Combine(data, "local-api.json"), """{"token":"ss_portable","port":1234,"running":true}""");

        Discovery.Find(null, _ => null, _directory)!.Token.Should().Be("ss_portable");
    }

    [Test]
    public void McpBridge_AnswersRequestsButNotNotificationsWhenTheAppIsMissing()
    {
        var reply = JsonNode.Parse(McpBridge.ErrorFor("""{"jsonrpc":"2.0","id":7,"method":"tools/list"}""", "not running")!)!;
        reply["id"]!.GetValue<int>().Should().Be(7);
        reply["error"]!["message"]!.GetValue<string>().Should().Be("not running");

        McpBridge.ErrorFor("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", "x").Should().BeNull();
    }

    [Test]
    public async Task McpBridge_ForwardsLinesAndRediscoversTheApp()
    {
        var input = new StringReader("""{"jsonrpc":"2.0","id":1,"method":"ping"}""" + "\n");
        var output = new StringWriter();
        var calls = 0;

        await McpBridge.RunAsync(() => { calls++; return null; }, input, output);

        calls.Should().Be(1);
        output.ToString().Should().Contain("\"id\":1").And.Contain("error");
    }
}
