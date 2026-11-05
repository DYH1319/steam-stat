using ElectronNet.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using SteamStat.Core.Events;
using SteamStat.Core.Features;

namespace ElectronNet.Tests.Services;

[TestFixture]
public sealed class TaskProgressPublisherTests
{
    [Test]
    public async Task Report_DropsRepeatsWithinTheIntervalButKeepsPhaseChangesAndCompletion()
    {
        var time = new SteppedTimeProvider();
        var bus = new RecordingEventBus();
        var progress = new TaskProgressPublisher(bus, time, NullLogger<TaskProgressPublisher>.Instance).For("librarySync");

        progress.Report(new TaskProgressUpdate("achievements", 0, 300));
        progress.Report(new TaskProgressUpdate("achievements", 100, 300));
        progress.Report(new TaskProgressUpdate("achievements", 300, 300));
        progress.Report(new TaskProgressUpdate("save"));
        time.Advance(TimeSpan.FromSeconds(1));
        progress.Report(new TaskProgressUpdate("save", 1));

        await bus.WaitForAsync(4);
        bus.Sent.Select(status => (status.Kind, status.Phase, status.Done, status.Total)).Should().Equal(
            ("librarySync", "achievements", 0, 300),
            ("librarySync", "achievements", 300, 300),
            ("librarySync", "save", 0, 0),
            ("librarySync", "save", 1, 0));
    }

    private sealed class SteppedTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    private sealed class RecordingEventBus : IEventBus
    {
        private readonly List<SteamStat.Contracts.Ipc.TaskProgressDto> _sent = [];

        public IReadOnlyList<SteamStat.Contracts.Ipc.TaskProgressDto> Sent
        {
            get { lock (_sent) return _sent.ToArray(); }
        }

        public Task PublishAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default)
            where TEvent : notnull
        {
            if (message is TaskProgressChanged changed)
                lock (_sent) _sent.Add(changed.Status);
            return Task.CompletedTask;
        }

        public async Task WaitForAsync(int count)
        {
            for (var attempt = 0; attempt < 200 && Sent.Count < count; attempt++)
                await Task.Delay(5);
        }
    }
}
