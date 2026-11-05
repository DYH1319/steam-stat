using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Events;
using SteamStat.Core.Features;

namespace ElectronNet.Services;

internal sealed record TaskProgressChanged(TaskProgressDto Status);

/// <summary>
/// 把长任务的进度转发给渲染层任务面板。阶段变化和完成时一定发送，其余按最小间隔丢弃，
/// 避免文件级的进度把 socket 桥占满。发送串行，保证渲染层收到的顺序与上报一致。
/// </summary>
internal sealed class TaskProgressPublisher(
    IEventBus eventBus,
    TimeProvider timeProvider,
    ILogger<TaskProgressPublisher> logger)
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(100);

    private readonly Lock _chainLock = new();
    private Task _chain = Task.CompletedTask;

    /// <summary>每次运行取一个新实例；kind 与渲染层 TaskKind 一致。</summary>
    public IProgress<TaskProgressUpdate> For(string kind) => new Reporter(this, kind);

    private DateTimeOffset Now => timeProvider.GetUtcNow();

    private void Publish(TaskProgressDto status)
    {
        lock (_chainLock)
        {
            _chain = _chain.ContinueWith(async _ =>
            {
                try
                {
                    await eventBus.PublishAsync(new TaskProgressChanged(status)).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    logger.LogDebug(exception, "Failed to publish task progress");
                }
            }, TaskScheduler.Default).Unwrap();
        }
    }

    private sealed class Reporter(TaskProgressPublisher owner, string kind) : IProgress<TaskProgressUpdate>
    {
        private readonly Lock _lock = new();
        private string? _phase;
        private DateTimeOffset _sentAt;

        public void Report(TaskProgressUpdate value)
        {
            lock (_lock)
            {
                var now = owner.Now;
                var finished = value.Total > 0 && value.Done >= value.Total;
                if (value.Phase == _phase && !finished && now - _sentAt < MinInterval) return;
                _phase = value.Phase;
                _sentAt = now;
                owner.Publish(new TaskProgressDto
                {
                    Kind = kind,
                    Phase = value.Phase,
                    Done = value.Done,
                    Total = value.Total
                });
            }
        }
    }
}
