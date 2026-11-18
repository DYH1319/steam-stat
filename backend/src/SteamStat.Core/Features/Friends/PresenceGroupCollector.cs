using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Features.Friends.Contracts;

namespace SteamStat.Core.Features.Friends;

/// <summary>
/// presence_group_record 的一行。<paramref name="GroupKey"/> 是哈希，空字符串表示离开了大厅或服务器。
/// </summary>
public sealed record PresenceGroupEntry(string AccountName, string SteamId, bool IsSelf, uint AppId, string Kind, string GroupKey, long ObservedAt);

public interface IPresenceGroupRecorder
{
    /// <summary>每个玩家每种分组的最后一条记录。</summary>
    Task<IReadOnlyList<PresenceGroupEntry>> GetLatestAsync(string accountName, CancellationToken cancellationToken = default);

    Task RecordAsync(PresenceGroupEntry entry, CancellationToken cancellationToken = default);
}

/// <summary>判定「在同一局」用到的三种分组。数值是持久化格式。</summary>
public static class PresenceGroupKinds
{
    /// <summary>CMsgClientPersonaState.game_lobby_id。</summary>
    public const string Lobby = "lobby";
    /// <summary>CMsgClientPersonaState.game_server_ip / port。</summary>
    public const string Server = "server";
    /// <summary>Rich Presence 的 steam_player_group，不另存，分析时从 rich_presence_record 读。</summary>
    public const string RichPresence = "rpGroup";

    public const string RichPresenceKey = "steam_player_group";
}

/// <summary>
/// 分组键只用来比较两个人是不是同一组，所以存哈希：服务器地址和大厅号对档案没有别的用处，存原值反而多一份别人的信息。
/// 应用 ID 在哈希里，不同游戏的同一个大厅号不会碰撞。
/// </summary>
public static class PresenceGroupKeys
{
    public static string Lobby(uint appId, ulong lobbyId)
        => appId == 0 || lobbyId == 0 ? string.Empty : Hash(PresenceGroupKinds.Lobby, appId, lobbyId.ToString(CultureInfo.InvariantCulture));

    public static string Server(uint appId, uint ip, uint port)
        => appId == 0 || ip == 0 || port == 0 ? string.Empty : Hash(PresenceGroupKinds.Server, appId, string.Create(CultureInfo.InvariantCulture, $"{ip}:{port}"));

    public static string RichPresence(uint appId, string? group)
        => appId == 0 || string.IsNullOrWhiteSpace(group) ? string.Empty : Hash(PresenceGroupKinds.RichPresence, appId, group.Trim());

    private static string Hash(string kind, uint appId, string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{kind}|{appId}|{value}")));
        return Convert.ToHexStringLower(bytes.AsSpan(0, 12));
    }
}

/// <summary>
/// 记录大厅和服务器的变化：账号本人和手动追踪的好友，值变化才写。
/// <para>
/// 回调在 CM 线程上，写库是异步的，所以所有账号共用一条有序队列，一个后台任务按顺序写。
/// 第一次看到一个账号时用数据库里的最后一条当基线，重启后不会把没变的状态再记一遍。
/// </para>
/// </summary>
public sealed class PresenceGroupCollector(
    IPresenceGroupRecorder recorder,
    IFriendStatusRecorder friendStatusRecorder,
    TimeProvider timeProvider,
    ILogger<PresenceGroupCollector> logger) : IAsyncDisposable
{
    private sealed record Observation(string AccountName, string SelfSteamId, SteamPersonaGroupUpdate Update, long ObservedAt);

    private readonly Channel<Observation> _queue = Channel.CreateUnbounded<Observation>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, Dictionary<(string SteamId, string Kind), string>?> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _startLock = new();
    private Task? _worker;
    private int _disposed;

    public void Observe(string accountName, string selfSteamId, SteamPersonaGroupUpdate update)
    {
        if (Volatile.Read(ref _disposed) != 0 || string.IsNullOrEmpty(accountName)) return;
        EnsureWorker();
        Interlocked.Increment(ref _pending);
        if (!_queue.Writer.TryWrite(new Observation(accountName, selfSteamId, update, timeProvider.GetUtcNow().ToUnixTimeSeconds())))
            Interlocked.Decrement(ref _pending);
    }

    /// <summary>会话结束：下次登录重新从数据库读基线。</summary>
    public void Forget(string accountName) => _known.TryRemove(accountName, out _);

    /// <summary>测试用：等队列里已有的观测处理完。</summary>
    internal async Task DrainAsync()
    {
        while (Volatile.Read(ref _pending) > 0) await Task.Delay(10).ConfigureAwait(false);
    }

    private int _pending;

    private void EnsureWorker()
    {
        if (_worker != null) return;
        lock (_startLock) _worker ??= Task.Run(() => RunAsync(_stopping.Token));
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var observation in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await ProcessAsync(observation, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception, "Failed to record a presence group change");
                }
                finally
                {
                    Interlocked.Decrement(ref _pending);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ProcessAsync(Observation observation, CancellationToken cancellationToken)
    {
        var update = observation.Update;
        var steamId = update.SteamId.ToString(CultureInfo.InvariantCulture);
        var isSelf = steamId == observation.SelfSteamId;
        if (!isSelf && !friendStatusRecorder.IsTracked(observation.AccountName, steamId)) return;

        var known = _known.GetValueOrDefault(observation.AccountName);
        if (known == null)
        {
            known = (await recorder.GetLatestAsync(observation.AccountName, cancellationToken).ConfigureAwait(false))
                .GroupBy(entry => (entry.SteamId, entry.Kind))
                .ToDictionary(group => group.Key, group => group.MaxBy(entry => entry.ObservedAt)!.GroupKey);
            _known[observation.AccountName] = known;
        }

        foreach (var (kind, key) in new[]
                 {
                     (PresenceGroupKinds.Lobby, PresenceGroupKeys.Lobby(update.AppId, update.LobbyId)),
                     (PresenceGroupKinds.Server, PresenceGroupKeys.Server(update.AppId, update.ServerIp, update.ServerPort))
                 })
        {
            var previous = known.GetValueOrDefault((steamId, kind)) ?? string.Empty;
            if (previous == key) continue;
            known[(steamId, kind)] = key;
            await recorder.RecordAsync(new PresenceGroupEntry(
                observation.AccountName, steamId, isSelf, update.AppId, kind, key, observation.ObservedAt), cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        if (_worker != null)
        {
            try
            {
                await _worker.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
            }
        }
        await _stopping.CancelAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }
}
