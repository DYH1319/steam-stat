using System.Security.Cryptography;
using System.Text;

namespace ElectronNet.Features.Archive;

/// <summary>存档目录中的一个文件</summary>
public sealed record SaveFileEntry(string RelativePath, string FullPath, long Size, long ModifiedAt);

/// <summary>清单中的一个文件（已计算哈希）</summary>
public sealed record SaveManifestEntry(string RelativePath, long Size, long ModifiedAt, string Sha256);

/// <summary>
/// 内容寻址的备份库：objects/{sha[0..2]}/{sha}。源文件只以只读 + 共享方式打开，绝不写回存档目录。
/// </summary>
public sealed class SaveBackupObjectStore(string root)
{
    public string Root { get; } = Path.GetFullPath(root);

    private string ObjectsDirectory => Path.Combine(Root, "objects");

    private string TempDirectory => Path.Combine(Root, "tmp");

    public string ExportsDirectory => Path.Combine(Root, "exports");

    public string ObjectPath(string sha256) => Path.Combine(ObjectsDirectory, sha256[..2], sha256);

    public bool Exists(string sha256) => File.Exists(ObjectPath(sha256));

    /// <summary>边读边算 SHA-256 并写入临时文件；内容已存在时丢弃临时文件。返回哈希与是否新增。</summary>
    public async Task<(string Sha256, bool Added)> StoreAsync(string sourceFile, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(TempDirectory);
        var temporary = Path.Combine(TempDirectory, Guid.NewGuid().ToString("N"));
        try
        {
            string sha;
            await using (var source = new FileStream(sourceFile, new FileStreamOptions
                         {
                             Mode = FileMode.Open,
                             Access = FileAccess.Read,
                             Share = FileShare.ReadWrite | FileShare.Delete,
                             Options = FileOptions.SequentialScan | FileOptions.Asynchronous
                         }))
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hasher.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                sha = Convert.ToHexStringLower(hasher.GetHashAndReset());
            }
            var destination = ObjectPath(sha);
            if (File.Exists(destination)) return (sha, false);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            try
            {
                File.Move(temporary, destination);
            }
            catch (IOException) when (File.Exists(destination))
            {
                return (sha, false);
            }
            File.SetAttributes(destination, FileAttributes.ReadOnly);
            return (sha, true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (IOException)
            {
                // 临时文件清理失败不影响结果
            }
        }
    }

    public byte[]? Read(string sha256, int maxBytes)
    {
        var path = ObjectPath(sha256);
        if (!File.Exists(path)) return null;
        var info = new FileInfo(path);
        if (info.Length > maxBytes) return null;
        return File.ReadAllBytes(path);
    }

    public void Delete(string sha256)
    {
        var path = ObjectPath(sha256);
        if (!File.Exists(path)) return;
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }

    /// <summary>
    /// 把一个版本还原到导出目录（备份库之外的新文件夹，绝不覆盖原存档目录）。
    /// </summary>
    public string Export(IEnumerable<SaveManifestEntry> files, string folderName)
    {
        var destination = Path.GetFullPath(Path.Combine(ExportsDirectory, folderName));
        Directory.CreateDirectory(destination);
        foreach (var file in files)
        {
            var target = Path.GetFullPath(Path.Combine(destination, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            var source = ObjectPath(file.Sha256);
            if (!File.Exists(source)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
            File.SetAttributes(target, FileAttributes.Normal);
            File.SetLastWriteTimeUtc(target, DateTimeOffset.FromUnixTimeSeconds(file.ModifiedAt).UtcDateTime);
        }
        return destination;
    }

    /// <summary>递归列出目录中的文件（跳过符号链接 / 联接点，避免循环与越界）。</summary>
    public static ScanOutcome Scan(string directory, int maxFiles, long maxBytes)
    {
        if (!Directory.Exists(directory)) return new ScanOutcome([], "notFound");
        var root = Path.GetFullPath(directory);
        var files = new List<SaveFileEntry>();
        long total = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", new EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         IgnoreInaccessible = true,
                         AttributesToSkip = FileAttributes.ReparsePoint
                     }))
            {
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                total += info.Length;
                if (files.Count >= maxFiles) return new ScanOutcome([], "tooManyFiles");
                if (total > maxBytes) return new ScanOutcome([], "tooLarge");
                files.Add(new SaveFileEntry(
                    Path.GetRelativePath(root, info.FullName).Replace('\\', '/'),
                    info.FullName,
                    info.Length,
                    new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds()));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ScanOutcome([], "unreadable");
        }
        return new ScanOutcome(files, null);
    }

    /// <summary>清单指纹：按相对路径排序后的 (路径, 哈希) 序列的 SHA-256。</summary>
    public static string ManifestHash(IEnumerable<SaveManifestEntry> files)
    {
        var builder = new StringBuilder();
        foreach (var file in files.OrderBy(file => file.RelativePath, StringComparer.Ordinal))
            builder.Append(file.RelativePath).Append('\0').Append(file.Sha256).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}

public sealed record ScanOutcome(IReadOnlyList<SaveFileEntry> Files, string? Error);
