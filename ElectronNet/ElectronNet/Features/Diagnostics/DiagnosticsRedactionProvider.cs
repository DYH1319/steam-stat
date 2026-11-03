using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Diagnostics;

namespace ElectronNet.Features.Diagnostics;

/// <summary>
/// 提供带本机身份信息（账号名、昵称、家目录、系统用户名）的脱敏器。
/// <see cref="Current"/> 不访问数据库，可在崩溃处理路径上安全调用；<see cref="RefreshAsync"/> 从数据库重新收集名称。
/// </summary>
internal sealed class DiagnosticsRedactionProvider(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ILogger<DiagnosticsRedactionProvider> logger)
{
    private volatile DiagnosticsRedactor _current = new(BaseContext([], []));

    public DiagnosticsRedactor Current => _current;

    public async Task<DiagnosticsRedactor> RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var users = await db.SteamUserTable.AsNoTracking()
                .Select(user => new { user.AccountName, user.PersonaName })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var tokenAccounts = await db.SteamLoginTokenTable.AsNoTracking()
                .Select(token => token.AccountName)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            var accounts = users.Select(user => user.AccountName).Concat(tokenAccounts).ToArray();
            var personas = users.Select(user => user.PersonaName ?? string.Empty).ToArray();
            _current = new DiagnosticsRedactor(BaseContext(accounts, personas));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Failed to collect account names for diagnostics redaction; using path-only redaction");
        }
        return _current;
    }

    private static DiagnosticsRedactionContext BaseContext(IReadOnlyCollection<string> accounts, IReadOnlyCollection<string> personas)
        => new(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName, accounts, personas);
}
