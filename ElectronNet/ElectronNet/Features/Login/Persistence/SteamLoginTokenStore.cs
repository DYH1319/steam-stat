using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using SteamStat.Core.Features.Login;
using SteamStat.Core.Platform;

namespace ElectronNet.Features.Login.Persistence;

internal sealed class SteamLoginTokenStore(IDbContextFactory<AppDbContext> dbContextFactory) : ISteamLoginTokenStore
{
    public SteamLoginTokenData? FindByAccountName(string accountName)
    {
        using var db = dbContextFactory.CreateDbContext();
        var token = db.SteamLoginTokenTable.AsNoTracking().FirstOrDefault(item => item.AccountName == accountName);
        return token == null ? null : Map(token);
    }

    public async Task<SteamLoginTokenData?> FindByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var token = await db.SteamLoginTokenTable.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        return token == null ? null : Map(token);
    }

    public IReadOnlyList<SteamLoginTokenData> List()
    {
        using var db = dbContextFactory.CreateDbContext();
        return db.SteamLoginTokenTable.AsNoTracking().ToList().Select(Map).ToArray();
    }

    public async Task UpsertAsync(SteamLoginTokenWrite token, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.SteamLoginTokenTable
            .FirstOrDefaultAsync(item => item.AccountName == token.AccountName, cancellationToken);
        if (existing == null)
        {
            db.SteamLoginTokenTable.Add(new SteamLoginToken
            {
                AccountName = token.AccountName,
                AccessToken = token.AccessToken,
                RefreshToken = token.RefreshToken,
                GuardData = token.GuardData,
                CreatedAt = token.CreatedAt
            });
        }
        else
        {
            existing.AccessToken = token.AccessToken;
            existing.RefreshToken = token.RefreshToken;
            existing.GuardData = token.GuardData ?? existing.GuardData;
            existing.CreatedAt = token.CreatedAt;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> EncryptLegacyAsync(ISecretStore secretStore, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var tokens = await db.SteamLoginTokenTable.ToListAsync(cancellationToken);
        var upgraded = 0;
        foreach (var token in tokens)
        {
            if (secretStore.IsProtected(token.AccessToken)
                && secretStore.IsProtected(token.RefreshToken)
                && (token.GuardData == null || secretStore.IsProtected(token.GuardData))) continue;

            token.AccessToken = secretStore.Protect(token.AccessToken) ?? token.AccessToken;
            token.RefreshToken = secretStore.Protect(token.RefreshToken) ?? token.RefreshToken;
            token.GuardData = secretStore.Protect(token.GuardData);
            upgraded++;
        }
        if (upgraded > 0) await db.SaveChangesAsync(cancellationToken);
        return upgraded;
    }

    public async Task<SteamLoginTokenData?> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var token = await db.SteamLoginTokenTable.FindAsync([id], cancellationToken);
        if (token == null) return null;
        db.SteamLoginTokenTable.Remove(token);
        await db.SaveChangesAsync(cancellationToken);
        return Map(token);
    }

    private static SteamLoginTokenData Map(SteamLoginToken token) => new(
        token.Id, token.AccountName, token.AccessToken, token.RefreshToken, token.GuardData, token.CreatedAt);
}
