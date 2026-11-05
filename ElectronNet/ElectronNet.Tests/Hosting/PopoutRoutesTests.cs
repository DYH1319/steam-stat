using System.Text.Json;
using System.Text.Json.Serialization;
using ElectronNet.Hosting;
using ElectronNet.Infrastructure;
using ElectronNET.Serialization;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SteamStat.Contracts.Ipc;

namespace ElectronNet.Tests.Hosting;

[TestFixture]
public sealed class PopoutRoutesTests
{
    [TestCase("/game/730", "game", "/game/730")]
    [TestCase("/game/730?tab=sessions", "game", "/game/730")]
    [TestCase("/friend/76561198000000000", "friend", "/friend/76561198000000000")]
    [TestCase("/friend/76561198000000000?account=some_name&tab=compare", "friend", "/friend/76561198000000000")]
    public void Parse_AcceptsDetailPagesAndKeysThemByPage(string path, string kind, string key)
    {
        var route = PopoutRoutes.Parse(path);

        route.Should().NotBeNull();
        route!.Kind.Should().Be(kind);
        route.Key.Should().Be(key);
        route.Path.Should().Be(path);
    }

    [Test]
    public void Parse_TreatsEachReportPeriodAsItsOwnWindow()
    {
        var week = PopoutRoutes.Parse("/reports?unit=week&anchor=2026-11-02");
        var year = PopoutRoutes.Parse("/reports?unit=year&anchor=2026-01-01");

        week!.Kind.Should().Be("report");
        week.Key.Should().NotBe(year!.Key);
    }

    [TestCase("")]
    [TestCase(null)]
    [TestCase("/setting")]
    [TestCase("/game/abc")]
    [TestCase("/game/730/../../setting")]
    [TestCase("https://example.com/game/730")]
    [TestCase("/game/730#/setting")]
    [TestCase("/game/730?x=\"><script>")]
    [TestCase("/reports/../setting")]
    public void Parse_RejectsEverythingElse(string? path)
    {
        PopoutRoutes.Parse(path).Should().BeNull();
    }

    /// <summary>Electron.NET 桥会把能解析成日期 / 时长 / GUID 的字符串改写，id 和路由必须原样到达 Host</summary>
    [TestCase("/game/730")]
    [TestCase("/reports?unit=week&anchor=2026-11-02")]
    [TestCase("/friend/76561198000000000?account=name")]
    public void OpenRequest_PathSurvivesTheElectronBridge(string path)
    {
        var request = ThroughBridge<PopoutOpenRequest>(new { path }, PopoutIpc.Open);

        request.Path.Should().Be(path);
    }

    [Test]
    public void ControlRequest_IdSurvivesTheElectronBridge()
    {
        var request = ThroughBridge<PopoutControlRequest>(new { id = "popout-12", action = "maximize" }, PopoutIpc.Control);

        request.Id.Should().Be("popout-12");
    }

    private static TRequest ThroughBridge<TRequest>(object payload, IIpcEndpointDescriptor endpoint)
    {
        var bridge = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase), new JsonToBoxedPrimitivesConverter() }
        };
        // 与 Electron.NET IpcMain.FormatArguments 相同：[event, args] → object[] → args
        var boxed = JsonSerializer.Deserialize<object[]>(JsonSerializer.Serialize(new[] { null, payload }), bridge)![1];
        return new IpcRequestBinder(NullLogger<IpcRequestBinder>.Instance).Bind<TRequest>(boxed, endpoint);
    }
}
