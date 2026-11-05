using FluentAssertions;
using SteamStat.Contracts.Ipc;

namespace SteamStat.Architecture.Tests;

/// <summary>错误码写在两个仓库里，渲染层不认识的错误码会被当成内部错误。</summary>
[TestFixture]
public sealed class IpcFailureContractTests
{
    [Test]
    public void Renderer_KnowsEveryFailureCode()
    {
        var env = Environment.GetEnvironmentVariable("STEAM_STAT_FRONTEND_DIR");
        if (string.IsNullOrWhiteSpace(env) || !Directory.Exists(env))
            Assert.Ignore("Renderer sources are not part of this repository; set STEAM_STAT_FRONTEND_DIR to the UI app directory to run these checks.");

        var source = File.ReadAllText(Path.Combine(env, "src", "utils", "ipcFailure.ts"));

        source.Should().Contain(IpcFailureCodes.MessagePrefix);
        foreach (var code in IpcFailureCodes.All) source.Should().Contain($"'{code}'");
    }
}
