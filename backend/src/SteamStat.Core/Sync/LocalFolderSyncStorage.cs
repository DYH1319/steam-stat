namespace SteamStat.Core.Sync;

/// <summary>
/// 本地文件夹（也可以是 NAS、U 盘，或由网盘客户端 / Syncthing 同步的目录）。
/// 先写同目录下的临时文件再改名，网盘客户端不会上传半个文件。
/// </summary>
public sealed class LocalFolderSyncStorage : ISyncStorage
{
    private const string TemporarySuffix = ".partial";
    private readonly string _root;

    public LocalFolderSyncStorage(string folder, string? remoteRoot = null)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
            throw new SyncStorageException(SyncStorageErrorKind.Configuration, "The sync folder must be an absolute path.");
        var root = SyncKeys.NormalizeRoot(remoteRoot);
        _root = Path.GetFullPath(root.Length == 0 ? folder : Path.Combine(folder, root.Replace('/', Path.DirectorySeparatorChar)));
        BaseFolder = Path.GetFullPath(folder);
    }

    public string BaseFolder { get; }

    public Task<IReadOnlyList<SyncObjectInfo>> ListAsync(string prefix, CancellationToken cancellationToken = default)
    {
        SyncKeys.ValidatePrefix(prefix);
        EnsureBaseFolder();
        var directory = prefix.Length == 0 ? _root : Path.Combine(_root, prefix.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(directory)) return Task.FromResult<IReadOnlyList<SyncObjectInfo>>([]);
        try
        {
            var result = new List<SyncObjectInfo>();
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file.EndsWith(TemporarySuffix, StringComparison.Ordinal)) continue;
                var key = Path.GetRelativePath(_root, file).Replace(Path.DirectorySeparatorChar, '/');
                if (SyncKeys.IsValid(key)) result.Add(new SyncObjectInfo(key, new FileInfo(file).Length));
            }
            result.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            return Task.FromResult<IReadOnlyList<SyncObjectInfo>>(result);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Wrap(exception);
        }
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = PathFor(key);
        try
        {
            return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false) : null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Wrap(exception);
        }
    }

    public async Task PutAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        var path = PathFor(key);
        EnsureBaseFolder();
        var temporary = $"{path}.{Guid.NewGuid():N}{TemporarySuffix}";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(temporary, content.ToArray(), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Wrap(exception);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (IOException)
            {
            }
        }
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = PathFor(key);
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return Task.CompletedTask;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Wrap(exception);
        }
    }

    private string PathFor(string key)
    {
        SyncKeys.Validate(key);
        return Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>所选的文件夹本身必须存在（例如 U 盘已拔出时不要在别处新建）。</summary>
    private void EnsureBaseFolder()
    {
        if (!Directory.Exists(BaseFolder))
            throw new SyncStorageException(SyncStorageErrorKind.Configuration, "The sync folder does not exist.");
    }

    private static SyncStorageException Wrap(Exception exception)
        => exception is UnauthorizedAccessException
            ? new SyncStorageException(SyncStorageErrorKind.Unauthorized, "Access to the sync folder was denied.", exception)
            : new SyncStorageException(SyncStorageErrorKind.Network, "The sync folder could not be accessed.", exception);
}
