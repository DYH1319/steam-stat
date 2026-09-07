using Microsoft.EntityFrameworkCore;
using SteamStat.Core.Platform;

namespace ElectronNet.Infrastructure;

internal sealed class ShellIpcPolicy(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ISteamInstallLocator installLocator)
{
    internal static bool IsAllowedExternalUrl(string value)
        => value.Length <= 2081
           && Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && uri.Scheme is "https" or "http"
           && string.IsNullOrEmpty(uri.UserInfo)
           && !string.IsNullOrWhiteSpace(uri.Host);

    internal bool IsAllowedPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 32767) return false;
        try
        {
            using var db = dbContextFactory.CreateDbContext();
            var allowedPaths = db.SteamAppTable.AsNoTracking()
                .Where(app => app.InstallDirPath != null)
                .Select(app => app.InstallDirPath!)
                .ToList();
            var steamPath = installLocator.ReadSteamRegistry().SteamPath;
            allowedPaths.AddRange(db.SteamUserTable.AsNoTracking()
                .Select(user => user.AccountId)
                .AsEnumerable()
                .Select(accountId => Path.Combine(steamPath, "userdata", accountId.ToString())));
            return IsAllowedPath(value, allowedPaths);
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsAllowedPath(string value, IEnumerable<string> allowedPaths)
    {
        if (!Path.IsPathFullyQualified(value)) return false;
        string candidate;
        try
        {
            candidate = Normalize(value);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        if (!Directory.Exists(candidate)) return false;
        return allowedPaths.Any(path =>
        {
            try
            {
                return string.Equals(candidate, Normalize(path), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        });
    }

    private static string Normalize(string value)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
}
