using ElectronNet.Hosting;
using ElectronNet.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Events;

namespace ElectronNet.Tests.Hosting;

[TestFixture]
public sealed class ElectronIpcEventForwarderTests
{
    [Test]
    public Task HandleAsync_WhenWindowIsMissing_SkipsAndLogs()
        => AssertUnavailableWindowIsSkipped(MainWindowAvailability.Missing);

    [Test]
    public Task HandleAsync_WhenWindowIsDestroyed_SkipsAndLogs()
        => AssertUnavailableWindowIsSkipped(MainWindowAvailability.Destroyed);

    private static async Task AssertUnavailableWindowIsSkipped(MainWindowAvailability availability)
    {
        var logger = new RecordingLogger<ElectronIpcEventForwarder>();
        var forwarder = new ElectronIpcEventForwarder(new StubMainWindowAccessor(availability), logger);

        var action = () => forwarder.HandleAsync(new LoginUsersChanged(), CancellationToken.None);

        await action.Should().NotThrowAsync();
        logger.Messages.Should().ContainSingle(message =>
            message.Contains(availability.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    private sealed class StubMainWindowAccessor(MainWindowAvailability availability) : IMainWindowAccessor
    {
        public Task<MainWindowSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new MainWindowSnapshot(null, availability));
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
