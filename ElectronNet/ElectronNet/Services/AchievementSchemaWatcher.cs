using Microsoft.Extensions.Logging;
using SteamStat.Core;
using SteamStat.Core.Events;
using SteamStat.Core.Features;
using SteamStat.Core.Features.Achievements;
using SteamStat.Core.Features.Achievements.Contracts;
using SteamStat.Core.Sessions;
using SteamStat.Core.Steam.Cache;
using SteamStat.Core.Steam.Gateway;

namespace ElectronNet.Services;

/// <summary>
/// 成就 schema 的变化不会自己送上门，晚一天就少记一天：登录后和每天巡检一次库里有成就的游戏。
/// 缓存新鲜的游戏不发请求，过期的只比较哈希。顺带重算成就集适配之前写的档案行。
/// </summary>
internal sealed class AchievementSchemaWatcher(
    SteamAchievementsService achievementsService,
    ISteamAchievementSchemaGateway schemaGateway,
    AchievementArchiveService archive,
    ILanguageProvider languageProvider,
    ISteamSessionAccessor sessionAccessor,
    TimeProvider timeProvider,
    ILogger<AchievementSchemaWatcher> logger) : IEventHandler<SteamSessionReady>, IDisposable
{
    private static readonly TimeSpan SessionReadyDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PassInterval = TimeSpan.FromHours(24);
    /// <summary>这是后台任务，请求之间留出间隔，不和用户的操作抢 CM。</summary>
    private static readonly TimeSpan RequestSpacing = TimeSpan.FromSeconds(1);

    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _timerLock = new();
    private readonly Dictionary<string, ITimer> _readyTimers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ITimer? _timer;
    private int _disposed;

    public void StartSchedule()
    {
        if (_timer != null) return;
        _timer = timeProvider.CreateTimer(_ =>
        {
            foreach (var accountName in sessionAccessor.GetLoggedInUsers()) _ = RunGuardedAsync(accountName);
        }, null, PassInterval, PassInterval);
    }

    public Task HandleAsync(SteamSessionReady message, CancellationToken cancellationToken)
    {
        lock (_timerLock)
        {
            if (_readyTimers.Remove(message.AccountName, out var previous)) previous.Dispose();
            _readyTimers[message.AccountName] = timeProvider.CreateTimer(_ =>
            {
                lock (_timerLock)
                {
                    if (_readyTimers.Remove(message.AccountName, out var timer)) timer.Dispose();
                }
                _ = RunGuardedAsync(message.AccountName);
            }, null, SessionReadyDelay, Timeout.InfiniteTimeSpan);
        }
        return Task.CompletedTask;
    }

    private async Task RunGuardedAsync(string accountName)
    {
        // 一次只跑一个账号：schema 是公共的，下一个账号大多命中缓存
        if (!await _gate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            await RunPassAsync(accountName, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Achievement schema pass failed for {AccountName}", accountName);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task RunPassAsync(string accountName, CancellationToken cancellationToken)
    {
        var overview = await achievementsService.GetOverviewAsync(accountName, cancellationToken).ConfigureAwait(false);
        if (!overview.ProgressState.HasValue) return;
        var recount = archive.GetAppsNeedingRecount(accountName);
        var language = languageProvider.GetSteamLanguage();
        foreach (var game in overview.Games
                     .Where(game => game.Progress is { Total: > 0 })
                     .OrderByDescending(game => game.LastPlayedAt))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!sessionAccessor.TryGetSession(accountName, out _)) return;
            var schema = await schemaGateway.GetSchemaAsync(
                game.AppId, language, accountName, SteamRefreshMode.PreferCache, cancellationToken).ConfigureAwait(false);
            var changed = schema is { IsSuccess: true, Value: { } value }
                && schema.Source != SteamDataSource.PublicData
                && await archive.ObserveSchemaAsync(value, cancellationToken).ConfigureAwait(false);
            var record = changed || recount.Contains(game.AppId);
            if (record) await archive.RecordGameAsync(accountName, game, cancellationToken).ConfigureAwait(false);
            if (record || schema.Source != SteamDataSource.Sqlite)
                await Task.Delay(RequestSpacing, timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        // 容器里注册了两个入口（服务本身和事件处理器），会释放两次
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        _timer?.Dispose();
        lock (_timerLock)
        {
            foreach (var timer in _readyTimers.Values) timer.Dispose();
            _readyTimers.Clear();
        }
        _lifetime.Dispose();
        _gate.Dispose();
    }
}
