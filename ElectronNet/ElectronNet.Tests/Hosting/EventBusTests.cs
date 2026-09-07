using ElectronNet.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Events;

namespace ElectronNet.Tests.Hosting;

[TestFixture]
public sealed class EventBusTests
{
    [Test]
    public async Task PublishAsync_InvokesRegisteredHandlerOnce()
    {
        var handler = new RecordingHandler();
        await using var provider = CreateProvider(services =>
            services.AddSingleton<IEventHandler<TestEvent>>(handler));

        await provider.GetRequiredService<IEventBus>().PublishAsync(new TestEvent("one"));

        handler.Messages.Should().Equal("one");
    }

    [Test]
    public async Task PublishAsync_InvokesEveryRegisteredHandler()
    {
        var first = new RecordingHandler();
        var second = new RecordingHandler();
        await using var provider = CreateProvider(services =>
        {
            services.AddSingleton<IEventHandler<TestEvent>>(first);
            services.AddSingleton<IEventHandler<TestEvent>>(second);
        });

        await provider.GetRequiredService<IEventBus>().PublishAsync(new TestEvent("all"));

        first.Messages.Should().Equal("all");
        second.Messages.Should().Equal("all");
    }

    [Test]
    public async Task PublishAsync_WhenCancelled_DoesNotExecuteRemainingHandlers()
    {
        using var cancellation = new CancellationTokenSource();
        var remaining = new RecordingHandler();
        await using var provider = CreateProvider(services =>
        {
            services.AddSingleton<IEventHandler<TestEvent>>(new CancellingHandler(cancellation));
            services.AddSingleton<IEventHandler<TestEvent>>(remaining);
        });

        var action = () => provider.GetRequiredService<IEventBus>()
            .PublishAsync(new TestEvent("cancel"), cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        remaining.Messages.Should().BeEmpty();
    }

    [Test]
    public async Task PublishAsync_WhenHandlerFails_ContinuesWithoutFailingPublisher()
    {
        var succeeding = new RecordingHandler();
        await using var provider = CreateProvider(services =>
        {
            services.AddSingleton<IEventHandler<TestEvent>>(new ThrowingHandler());
            services.AddSingleton<IEventHandler<TestEvent>>(succeeding);
        });

        var action = () => provider.GetRequiredService<IEventBus>().PublishAsync(new TestEvent("safe"));

        await action.Should().NotThrowAsync();
        succeeding.Messages.Should().Equal("safe");
    }

    private static ServiceProvider CreateProvider(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEventBus, InProcessEventBus>();
        configure(services);
        return services.BuildServiceProvider();
    }

    private sealed record TestEvent(string Value);

    private sealed class RecordingHandler : IEventHandler<TestEvent>
    {
        public List<string> Messages { get; } = [];

        public Task HandleAsync(TestEvent message, CancellationToken cancellationToken)
        {
            Messages.Add(message.Value);
            return Task.CompletedTask;
        }
    }

    private sealed class CancellingHandler(CancellationTokenSource cancellation) : IEventHandler<TestEvent>
    {
        public Task HandleAsync(TestEvent message, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingHandler : IEventHandler<TestEvent>
    {
        public Task HandleAsync(TestEvent message, CancellationToken cancellationToken)
            => throw new InvalidOperationException("expected test failure");
    }
}
