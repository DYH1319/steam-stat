using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ElectronNet.Persistence;

public sealed class AppDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    private const string DatabaseEnvironmentVariable = "STEAM_STAT_DESIGN_DATABASE";

    public AppDbContext CreateDbContext(string[] args)
    {
        var databaseFile = GetDatabaseFile(args);
        var databaseDirectory = Path.GetDirectoryName(databaseFile)
            ?? throw new InvalidOperationException("The design-time database path must include a directory.");
        Directory.CreateDirectory(databaseDirectory);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(SqliteConnectionStrings.Create(databaseFile))
            .Options;
        return new AppDbContext(options);
    }

    private static string GetDatabaseFile(string[] args)
    {
        var databaseArgumentIndex = Array.FindIndex(args, value => value.Equals("--database", StringComparison.OrdinalIgnoreCase));
        if (databaseArgumentIndex >= 0)
        {
            if (databaseArgumentIndex == args.Length - 1 || string.IsNullOrWhiteSpace(args[databaseArgumentIndex + 1]))
            {
                throw new ArgumentException("The --database option requires a path.", nameof(args));
            }

            return Path.GetFullPath(args[databaseArgumentIndex + 1]);
        }

        var configuredPath = Environment.GetEnvironmentVariable(DatabaseEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredPath)) return Path.GetFullPath(configuredPath);

        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), ".ef", "steam-stat.db"));
    }
}
