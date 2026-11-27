using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ElectronNet.Features.Nintendo;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Events;
using SteamStat.Core.Features;
using SteamStat.Core.Insights;
using SteamStat.Core.Nintendo;
using SteamStat.Core.Platform;
using SteamStat.Core.Settings;

namespace ElectronNet.Services;

internal sealed record NintendoStatusChanged(NintendoStatusDto Status);

internal sealed record NintendoLoginFinished(NintendoLoginResultDto Result);

/// <summary>周刊的 Nintendo 一节。</summary>
internal interface INintendoWeeklySource
{
    Task<WeeklyNintendo?> SummarizeAsync(string fromDay, string toDay, CancellationToken cancellationToken = default);
}

/// <summary>
/// Nintendo 游玩记录（实验功能，默认关闭）：绑定任天堂账号、在应用运行期间定时读取 Play Activity、
/// 给渲染层紧凑数据集。没启用或没确认风险说明时不发任何请求。
/// 登录凭证经 <see cref="ISecretStore"/> 加密存在 nintendo_credential（不能安全保存时只留在内存里），
/// 不进设置、同步、备份、导出和诊断包。
/// </summary>
internal sealed class NintendoService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    NintendoClient client,
    ISecretStore secretStore,
    ISettingsStore settingsStore,
    INintendoLoginWindow loginWindow,
    IEventBus eventBus,
    TimeProvider timeProvider,
    ILogger<NintendoService> logger) : INintendoWeeklySource, IDisposable
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LoginLifetime = TimeSpan.FromMinutes(30);
    private const int WeeklyTop = 3;

    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _memoryTokens = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _backgroundSyncs = [];
    private readonly CancellationTokenSource _lifetime = new();
    private (string Id, NintendoLoginRequest Request, DateTimeOffset CreatedAt)? _login;
    private ITimer? _timer;
    private long _lastRunAt;
    private volatile bool _syncing;
    private int _disposed;

    private long Now => timeProvider.GetUtcNow().ToUnixTimeSeconds();

    private bool Active => NintendoSettings.IsActive(settingsStore.GetSettings());

    public void StartSchedule()
        => _timer ??= timeProvider.CreateTimer(_ => _ = TickAsync(), null, InitialDelay, TickInterval);

    private async Task TickAsync()
    {
        try
        {
            var settings = settingsStore.GetSettings();
            if (!NintendoSettings.IsActive(settings)) return;
            var interval = (settings.Nintendo?.PollMinutes ?? 15) * 60L;
            if (Now - Interlocked.Read(ref _lastRunAt) < interval) return;
            await SyncAsync(null, null, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Nintendo sync tick failed");
        }
    }

    #region 状态与账号

    public async Task<NintendoStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var accounts = await db.NintendoAccountTable.AsNoTracking().OrderBy(row => row.LinkedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        var credentials = await db.NintendoCredentialTable.AsNoTracking().Select(row => row.NaId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var signedIn = credentials.ToHashSet(StringComparer.Ordinal);
        lock (_lock) signedIn.UnionWith(_memoryTokens.Keys);
        return new NintendoStatusDto
        {
            Active = Active,
            CanPersist = secretStore.CanPersist,
            Syncing = _syncing,
            Accounts = accounts.Select(account => ToDto(account, signedIn.Contains(account.NaId))).ToList()
        };
    }

    private static NintendoAccountDto ToDto(NintendoAccount account, bool signedIn) => new()
    {
        NaId = account.NaId,
        Nickname = account.Nickname,
        IconUrl = account.IconUrl,
        Country = account.Country,
        Mine = account.Mine,
        LinkedAt = account.LinkedAt,
        LastSyncAt = account.LastSyncAt,
        LastSuccessAt = account.LastSuccessAt,
        State = !signedIn ? NintendoStates.SignedOut
            : account.LastError ?? (account.LastSuccessAt == null ? NintendoStates.Pending : NintendoStates.Ok)
    };

    public async Task<NintendoStatusDto> UpdateAccountAsync(NintendoAccountUpdateRequest request, CancellationToken cancellationToken = default)
    {
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var account = await db.NintendoAccountTable.FirstOrDefaultAsync(row => row.NaId == request.NaId, cancellationToken).ConfigureAwait(false);
            if (account != null && request.Mine is { } mine && account.Mine != mine)
            {
                account.Mine = mine;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        return await PublishStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>解绑：一定删除登录；DeleteData 时连同这个账号的时长数据一起删（游戏名称和封面是共用的，留着）。</summary>
    public async Task<NintendoStatusDto> UnlinkAccountAsync(NintendoAccountUnlinkRequest request, CancellationToken cancellationToken = default)
    {
        lock (_lock) _memoryTokens.Remove(request.NaId);
        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            await db.NintendoCredentialTable.Where(row => row.NaId == request.NaId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            if (request.DeleteData)
            {
                await db.NintendoDailyPlaytimeTable.Where(row => row.NaId == request.NaId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await db.NintendoPlaytimeSnapshotTable.Where(row => row.NaId == request.NaId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await db.NintendoTitleTotalTable.Where(row => row.NaId == request.NaId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await db.NintendoAccountTable.Where(row => row.NaId == request.NaId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        logger.LogInformation("Unlinked a Nintendo account (data deleted: {Deleted})", request.DeleteData);
        return await PublishStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<NintendoStatusDto> PublishStatusAsync(CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        await eventBus.PublishAsync(new NintendoStatusChanged(status), cancellationToken).ConfigureAwait(false);
        return status;
    }

    #endregion

    #region 登录

    /// <summary>开始一次登录：生成 PKCE，打开应用内登录窗口。同一时间只有一次登录，新的会让旧的失效。</summary>
    public async Task<NintendoLoginBeginDto> BeginLoginAsync(CancellationToken cancellationToken = default)
    {
        var request = NintendoLoginRequest.Create();
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
        lock (_lock) _login = (id, request, timeProvider.GetUtcNow());
        if (!Active) return new NintendoLoginBeginDto { LoginId = id, Url = request.Url.AbsoluteUri, WindowOpened = false };

        var opened = await loginWindow.OpenAsync(
            request.Url,
            callback => _ = FinishFromWindowAsync(id, callback),
            () => _ = AnnounceAsync(new NintendoLoginResultDto { LoginId = id, Status = "cancelled" }),
            cancellationToken).ConfigureAwait(false);
        return new NintendoLoginBeginDto { LoginId = id, Url = request.Url.AbsoluteUri, WindowOpened = opened };
    }

    public Task<bool> CancelLoginAsync(string loginId)
    {
        var cancelled = false;
        lock (_lock)
        {
            if (_login?.Id == loginId)
            {
                _login = null;
                cancelled = true;
            }
        }
        if (cancelled) loginWindow.Close();
        return Task.FromResult(cancelled);
    }

    private async Task FinishFromWindowAsync(string loginId, string callback)
    {
        try
        {
            var result = await CompleteLoginAsync(new NintendoLoginCompleteRequest { LoginId = loginId, CallbackUrl = callback }, _lifetime.Token).ConfigureAwait(false);
            await AnnounceAsync(result).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Nintendo login from the window failed");
            await AnnounceAsync(new NintendoLoginResultDto { LoginId = loginId, Status = "failed" }).ConfigureAwait(false);
        }
    }

    private async Task AnnounceAsync(NintendoLoginResultDto result)
    {
        try
        {
            await eventBus.PublishAsync(new NintendoLoginFinished(result)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Failed to announce the Nintendo login result");
        }
    }

    private void QueuePostLoginSync(IReadOnlyList<string> naIds)
    {
        var task = Task.Run(async () =>
        {
            try
            {
                await SyncAsync(naIds, null, _lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Nintendo sync after login failed");
            }
        }, CancellationToken.None);
        lock (_lock) _backgroundSyncs.Add(task);
        _ = task.ContinueWith(done =>
        {
            lock (_lock) _backgroundSyncs.Remove(done);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>用回调里的 session_token_code 换 session token、读账号资料、保存登录，然后马上读一次游玩记录。</summary>
    public async Task<NintendoLoginResultDto> CompleteLoginAsync(NintendoLoginCompleteRequest request, CancellationToken cancellationToken = default)
    {
        NintendoLoginResultDto Result(string status, NintendoAccountDto? account = null)
            => new() { LoginId = request.LoginId, Status = status, Account = account };

        if (!Active) return Result("inactive");
        NintendoLoginRequest? login;
        lock (_lock)
        {
            login = _login is { } current && current.Id == request.LoginId && timeProvider.GetUtcNow() - current.CreatedAt < LoginLifetime
                ? current.Request
                : null;
        }
        if (login == null) return Result("expired");

        var callback = NintendoCallback.Parse(request.CallbackUrl, login.State);
        if (callback.Error != null) return Result(callback.Error);
        lock (_lock)
        {
            // 一个 code 只能换一次：先作废这次登录，重复提交直接按过期处理
            if (_login?.Id != request.LoginId) return Result("expired");
            _login = null;
        }
        loginWindow.Close();

        try
        {
            var sessionToken = await client.ExchangeSessionTokenAsync(callback.Code!, login.CodeVerifier, cancellationToken).ConfigureAwait(false);
            var tokens = await client.GetTokensAsync(sessionToken, cancellationToken).ConfigureAwait(false);
            var user = await client.GetUserAsync(tokens, cancellationToken).ConfigureAwait(false);
            var (account, existed) = await SaveLoginAsync(user, sessionToken, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Linked a Nintendo account (existing: {Existing})", existed);
            QueuePostLoginSync([user.Id]);
            await PublishStatusAsync(cancellationToken).ConfigureAwait(false);
            return Result(existed ? "relinked" : "linked", ToDto(account, true));
        }
        catch (NintendoAuthException)
        {
            return Result("expired");
        }
        catch (Exception exception) when (IsNetwork(exception) && !cancellationToken.IsCancellationRequested)
        {
            return Result("network");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Nintendo login failed");
            return Result("failed");
        }
    }

    private async Task<(NintendoAccount Account, bool Existed)> SaveLoginAsync(NintendoUser user, string sessionToken, CancellationToken cancellationToken)
    {
        var now = Now;
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var account = await db.NintendoAccountTable.FirstOrDefaultAsync(row => row.NaId == user.Id, cancellationToken).ConfigureAwait(false);
        var existed = account != null;
        if (account == null)
        {
            // 第一个绑定的账号默认是「我」，之后的（家人、小号）要用户自己标
            var first = !await db.NintendoAccountTable.AnyAsync(cancellationToken).ConfigureAwait(false);
            account = new NintendoAccount { NaId = user.Id, Mine = first, LinkedAt = now };
            db.NintendoAccountTable.Add(account);
        }
        account.Nickname = user.Nickname;
        account.IconUrl = user.IconUrl;
        account.Country = user.Country;
        account.LastError = null;

        var stored = secretStore.CanPersist ? secretStore.Protect(sessionToken) : null;
        var credential = await db.NintendoCredentialTable.FirstOrDefaultAsync(row => row.NaId == user.Id, cancellationToken).ConfigureAwait(false);
        if (stored != null)
        {
            if (credential == null) db.NintendoCredentialTable.Add(new NintendoCredential { NaId = user.Id, SessionToken = stored, UpdatedAt = now });
            else
            {
                credential.SessionToken = stored;
                credential.UpdatedAt = now;
            }
        }
        else if (credential != null)
        {
            db.NintendoCredentialTable.Remove(credential);
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        lock (_lock) _memoryTokens[user.Id] = sessionToken;
        return (account, existed);
    }

    private async Task<string?> SessionTokenAsync(AppDbContext db, string naId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_memoryTokens.TryGetValue(naId, out var cached)) return cached;
        }
        var stored = await db.NintendoCredentialTable.AsNoTracking().Where(row => row.NaId == naId).Select(row => row.SessionToken).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var token = string.IsNullOrEmpty(stored) ? null : secretStore.Unprotect(stored);
        if (token != null) lock (_lock) _memoryTokens[naId] = token;
        return token;
    }

    #endregion

    #region 读取

    /// <summary>读一遍游玩记录（全部账号或指定账号）。和定时读取共用一把锁，同时只跑一次。</summary>
    public async Task<NintendoSyncResultDto> SyncAsync(IReadOnlyList<string>? naIds, IProgress<TaskProgressUpdate>? progress, CancellationToken cancellationToken = default)
    {
        NintendoSyncResultDto Result(string status, int accounts = 0, int titles = 0, int days = 0)
            => new() { Status = status, Accounts = accounts, Titles = titles, Days = days };

        var settings = settingsStore.GetSettings();
        if (!NintendoSettings.IsActive(settings)) return Result("inactive");
        await _syncGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _syncing = true;
        try
        {
            Interlocked.Exchange(ref _lastRunAt, Now);
            List<NintendoAccount> accounts;
            await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
            {
                accounts = await db.NintendoAccountTable.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
            }
            if (naIds is { Count: > 0 }) accounts = accounts.Where(account => naIds.Contains(account.NaId)).ToList();
            progress?.Report(new TaskProgressUpdate("fetch", 0, accounts.Count));

            var locales = NintendoEndpoints.LocalesFor(settings.Language);
            int ok = 0, failed = 0, titles = 0, days = 0;
            foreach (var account in accounts)
            {
                var outcome = await SyncAccountAsync(account, locales, cancellationToken).ConfigureAwait(false);
                if (outcome is { } write)
                {
                    ok++;
                    titles += write.Titles;
                    days += write.Days;
                }
                else failed++;
                progress?.Report(new TaskProgressUpdate("fetch", ok + failed, accounts.Count));
            }
            var status = accounts.Count == 0 ? "noAccounts" : failed == 0 ? "ok" : ok == 0 ? "failed" : "partial";
            return Result(status, ok, titles, days);
        }
        finally
        {
            _syncing = false;
            _syncGate.Release();
            try
            {
                await PublishStatusAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Failed to publish the Nintendo status");
            }
        }
    }

    private async Task<NintendoWriteResult?> SyncAccountAsync(NintendoAccount account, IReadOnlyList<string> locales, CancellationToken cancellationToken)
    {
        string? error;
        NintendoWriteResult? result = null;
        var now = Now;
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var sessionToken = await SessionTokenAsync(db, account.NaId, cancellationToken).ConfigureAwait(false);
        if (sessionToken == null)
        {
            // 没有登录（换了设备或令牌被撤销）：不发请求，等用户重新登录
            return null;
        }
        try
        {
            var tokens = await client.GetTokensAsync(sessionToken, cancellationToken).ConfigureAwait(false);
            var history = await client.GetPlayHistoryAsync(tokens, locales, cancellationToken).ConfigureAwait(false);
            result = await NintendoArchiveWriter.ApplyAsync(db, account.NaId, history, now, account.LastSuccessAt, cancellationToken).ConfigureAwait(false);
            error = null;
        }
        catch (NintendoAuthException)
        {
            // 令牌失效：删掉没用的登录，账号显示为需要重新登录
            lock (_lock) _memoryTokens.Remove(account.NaId);
            await db.NintendoCredentialTable.Where(row => row.NaId == account.NaId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            error = NintendoStates.SignedOut;
            logger.LogInformation("A Nintendo login is no longer valid");
        }
        catch (NintendoFormatException exception)
        {
            error = NintendoStates.Layout;
            logger.LogWarning(exception, "Nintendo play history had an unexpected shape");
        }
        catch (Exception exception) when (IsNetwork(exception) && !cancellationToken.IsCancellationRequested)
        {
            error = NintendoStates.Network;
            logger.LogInformation("Nintendo play history is unreachable: {Error}", exception.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            error = NintendoStates.Failed;
            logger.LogWarning(exception, "Nintendo sync failed");
        }

        await db.NintendoAccountTable
            .Where(row => row.NaId == account.NaId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.LastSyncAt, now)
                .SetProperty(row => row.LastError, error == NintendoStates.SignedOut ? null : error)
                .SetProperty(row => row.LastSuccessAt, row => error == null ? now : row.LastSuccessAt), cancellationToken)
            .ConfigureAwait(false);
        return error == null ? result : null;
    }

    private static bool IsNetwork(Exception exception)
        => exception is HttpRequestException or SocketException or TimeoutException
           || exception is TaskCanceledException { InnerException: TimeoutException };

    #endregion

    #region 数据集

    public async Task<NintendoRevisionDto> GetRevisionAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var daily = await db.NintendoDailyPlaytimeTable.AsNoTracking().GroupBy(_ => 1)
            .Select(group => new { Count = group.Count(), Sum = group.Sum(row => (long)row.Minutes), Max = group.Max(row => row.ObservedAt) })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var totals = await db.NintendoTitleTotalTable.AsNoTracking().GroupBy(_ => 1)
            .Select(group => new { Count = group.Count(), Sum = group.Sum(row => (long)row.TotalMinutes), Max = group.Max(row => row.UpdatedAt) })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var snapshots = await db.NintendoPlaytimeSnapshotTable.AsNoTracking().GroupBy(_ => 1)
            .Select(group => new { Count = group.Count(), Max = group.Max(row => row.LastObservedAt) })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var titles = await db.NintendoTitleTable.AsNoTracking().GroupBy(_ => 1)
            .Select(group => new { Count = group.Count(), Max = group.Max(row => row.UpdatedAt) })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var accounts = await db.NintendoAccountTable.AsNoTracking().OrderBy(row => row.NaId).Select(row => row.NaId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var parts = string.Join('|',
            daily?.Count, daily?.Sum, daily?.Max, totals?.Count, totals?.Sum, totals?.Max,
            snapshots?.Count, snapshots?.Max, titles?.Count, titles?.Max, string.Join(',', accounts));
        return new NintendoRevisionDto { Revision = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(parts)))[..16] };
    }

    public async Task<NintendoDatasetDto> GetDatasetAsync(NintendoScopeRequest request, CancellationToken cancellationToken = default)
    {
        var scope = request.NaIds is { Count: > 0 } ids ? ids.ToHashSet(StringComparer.Ordinal) : null;
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var totals = await Scoped(db.NintendoTitleTotalTable.AsNoTracking(), scope).ToListAsync(cancellationToken).ConfigureAwait(false);
        var daily = await Scoped(db.NintendoDailyPlaytimeTable.AsNoTracking(), scope).OrderBy(row => row.PlayedDay).ToListAsync(cancellationToken).ConfigureAwait(false);
        var segments = await Scoped(db.NintendoPlaytimeSnapshotTable.AsNoTracking(), scope).ToListAsync(cancellationToken).ConfigureAwait(false);
        var titles = await db.NintendoTitleTable.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);

        var accounts = totals.Select(row => row.NaId).Concat(daily.Select(row => row.NaId)).Distinct().Order(StringComparer.Ordinal).ToList();
        var accountIndex = accounts.Select((id, index) => (id, index)).ToDictionary(pair => pair.id, pair => pair.index, StringComparer.Ordinal);
        var known = titles.ToDictionary(row => (row.TitleId, row.Platform));
        var used = totals.Select(row => (row.TitleId, row.Platform)).Concat(daily.Select(row => (row.TitleId, row.Platform))).Distinct()
            .OrderBy(key => key.TitleId, StringComparer.Ordinal).ThenBy(key => key.Platform, StringComparer.Ordinal).ToList();
        var titleIndex = used.Select((key, index) => (key, index)).ToDictionary(pair => pair.key, pair => pair.index);

        var flatDaily = new List<int>(daily.Count * 4);
        foreach (var row in daily)
            flatDaily.AddRange([titleIndex[(row.TitleId, row.Platform)], accountIndex[row.NaId], row.PlayedDay, row.Minutes]);

        var flatIntervals = new List<int>();
        var placed = 0;
        var unplaced = 0;
        foreach (var group in segments.GroupBy(row => row.NaId))
        {
            var inference = NintendoPlaytimeInference.Infer(group.Select(row => new NintendoSegment(row.TitleId, row.Platform, row.TotalMinutes, row.FirstObservedAt, row.LastObservedAt, row.LastPlayedAt)));
            placed += inference.PlacedMinutes;
            unplaced += inference.UnplacedMinutes;
            if (!accountIndex.TryGetValue(group.Key, out var account)) continue;
            foreach (var interval in inference.Intervals)
            {
                if (!titleIndex.TryGetValue((interval.TitleId, interval.Platform), out var title)) continue;
                flatIntervals.AddRange([title, account, (int)interval.Start, (int)interval.End, interval.Minutes]);
            }
        }

        return new NintendoDatasetDto
        {
            Accounts = accounts,
            Titles = used.Select(key => known.TryGetValue(key, out var row)
                ? new NintendoTitleDto { TitleId = row.TitleId, Platform = row.Platform, Name = row.Name, ImageUrl = row.ImageUrl }
                : new NintendoTitleDto { TitleId = key.TitleId, Platform = key.Platform, Name = key.TitleId }).ToList(),
            Totals = totals.Select(row => new NintendoTotalDto
            {
                Title = titleIndex[(row.TitleId, row.Platform)],
                Account = accountIndex[row.NaId],
                TotalMinutes = row.TotalMinutes,
                TotalDays = row.TotalDays,
                FirstPlayedAt = row.FirstPlayedAt,
                LastPlayedAt = row.LastPlayedAt
            }).ToList(),
            Daily = flatDaily,
            Intervals = flatIntervals,
            PlacedMinutes = placed,
            UnplacedMinutes = unplaced
        };
    }

    public async Task<IReadOnlyList<NintendoSegmentDto>> GetTitleHistoryAsync(NintendoTitleHistoryRequest request, CancellationToken cancellationToken = default)
    {
        var scope = request.NaIds is { Count: > 0 } ids ? ids.ToHashSet(StringComparer.Ordinal) : null;
        var titleId = request.TitleId.Trim().ToLowerInvariant();
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await Scoped(db.NintendoPlaytimeSnapshotTable.AsNoTracking(), scope)
            .Where(row => row.TitleId == titleId)
            .OrderBy(row => row.FirstObservedAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(row => new NintendoSegmentDto
        {
            NaId = row.NaId,
            Platform = row.Platform,
            Minutes = row.TotalMinutes,
            FirstObservedAt = row.FirstObservedAt,
            LastObservedAt = row.LastObservedAt
        }).ToList();
    }

    private static IQueryable<NintendoTitleTotal> Scoped(IQueryable<NintendoTitleTotal> query, HashSet<string>? scope)
        => scope == null ? query : query.Where(row => scope.Contains(row.NaId));

    private static IQueryable<NintendoDailyPlaytime> Scoped(IQueryable<NintendoDailyPlaytime> query, HashSet<string>? scope)
        => scope == null ? query : query.Where(row => scope.Contains(row.NaId));

    private static IQueryable<NintendoPlaytimeSnapshot> Scoped(IQueryable<NintendoPlaytimeSnapshot> query, HashSet<string>? scope)
        => scope == null ? query : query.Where(row => scope.Contains(row.NaId));

    #endregion

    #region 周刊

    /// <summary>
    /// 周刊的 Nintendo 一节：只算「我」的账号（一个都没标时算全部），隐藏的游戏不算。
    /// 没启用、这周没有日级数据时返回 null，这一期就没有这一节。
    /// </summary>
    public async Task<WeeklyNintendo?> SummarizeAsync(string fromDay, string toDay, CancellationToken cancellationToken = default)
    {
        var settings = settingsStore.GetSettings();
        if (!NintendoSettings.IsActive(settings)) return null;
        if (NintendoArchiveWriter.Day(fromDay) is not { } from || NintendoArchiveWriter.Day(toDay) is not { } to) return null;
        var hidden = (settings.Nintendo?.HiddenTitleIds ?? []).ToHashSet(StringComparer.Ordinal);

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var accounts = await db.NintendoAccountTable.AsNoTracking().Select(row => new { row.NaId, row.Mine }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var mine = accounts.Where(row => row.Mine).Select(row => row.NaId).ToList();
        if (mine.Count == 0) mine = accounts.Select(row => row.NaId).ToList();
        if (mine.Count == 0) return null;

        var rows = (await db.NintendoDailyPlaytimeTable.AsNoTracking()
                .Where(row => mine.Contains(row.NaId) && row.PlayedDay >= from && row.PlayedDay <= to && row.Minutes > 0)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Where(row => !hidden.Contains(row.TitleId))
            .ToList();
        if (rows.Count == 0) return null;

        var names = (await db.NintendoTitleTable.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(row => (row.TitleId, row.Platform));
        var top = rows.GroupBy(row => (row.TitleId, row.Platform))
            .Select(group => (group.Key, Seconds: group.Sum(row => (long)row.Minutes) * 60))
            .OrderByDescending(item => item.Seconds)
            .ThenBy(item => item.Key.TitleId, StringComparer.Ordinal)
            .Take(WeeklyTop)
            .Select(item =>
            {
                var title = names.GetValueOrDefault(item.Key);
                return new WeeklyNintendoTitle(item.Key.TitleId, item.Key.Platform, title?.Name ?? item.Key.TitleId, title?.ImageUrl, item.Seconds);
            })
            .ToList();
        return new WeeklyNintendo(
            rows.Sum(row => (long)row.Minutes) * 60,
            rows.Select(row => (row.TitleId, row.Platform)).Distinct().Count(),
            rows.Select(row => row.PlayedDay).Distinct().Count(),
            top);
    }

    #endregion

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer?.Dispose();
        _lifetime.Cancel();
        Task pending;
        lock (_lock) pending = _backgroundSyncs.Count == 0 ? Task.CompletedTask : Task.WhenAll(_backgroundSyncs);
        try
        {
            pending.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Failed while waiting for Nintendo background syncs");
        }
        _lifetime.Dispose();
        if (pending.IsCompleted) _syncGate.Dispose();
    }
}
