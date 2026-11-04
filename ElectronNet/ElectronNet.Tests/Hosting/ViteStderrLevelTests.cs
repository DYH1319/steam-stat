using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace ElectronNet.Tests.Hosting;

[TestFixture]
public sealed class ViteStderrLevelTests
{
    [TestCase("$ vite", LogLevel.Debug)]
    [TestCase("$ vite --mode development", LogLevel.Debug)]
    [TestCase("error when starting dev server:", LogLevel.Warning)]
    [TestCase("[vite] Internal server error: Failed to resolve import", LogLevel.Warning)]
    public void PnpmCommandEchoIsNotAWarning(string line, LogLevel expected)
        => Program.ViteStderrLevel(line).Should().Be(expected);
}
