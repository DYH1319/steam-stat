using Microsoft.Data.Sqlite;

namespace ElectronNet.Persistence;

internal static class SqliteConnectionStrings
{
    internal static string Create(string databaseFile, bool pooling = true)
        => new SqliteConnectionStringBuilder
        {
            Mode = SqliteOpenMode.ReadWriteCreate,
            DataSource = databaseFile,
            Cache = SqliteCacheMode.Default,
            ForeignKeys = null,
            RecursiveTriggers = false,
            DefaultTimeout = 10,
            Pooling = pooling,
            Vfs = null
        }.ToString();
}
