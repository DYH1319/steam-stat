using System.Text;

namespace SteamStat.Core.Helpers;

public static class PathHelper
{
    /// <summary>
    /// 将路径规范化为 Windows 资源管理器显示的真实大小写格式：
    /// 盘符大写、分隔符为反斜杠，已存在的目录/文件段按磁盘上的实际大小写解析。
    /// 不存在的段与通配符段（* ?）原样保留；非法路径原样返回。
    /// </summary>
    public static string ToTrueCase(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
            {
                return path;
            }

            var rootDisplay = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (rootDisplay.Length == 2 && rootDisplay[1] == ':')
            {
                rootDisplay = char.ToUpperInvariant(rootDisplay[0]) + rootDisplay.Substring(1);
            }

            var builder = new StringBuilder(rootDisplay, fullPath.Length);
            var current = new DirectoryInfo(root);
            var resolvable = true;
            foreach (var segment in fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                builder.Append(Path.DirectorySeparatorChar);

                FileSystemInfo? entry = null;
                if (resolvable && segment.IndexOfAny('*', '?') < 0 && current.Exists)
                {
                    entry = current.EnumerateFileSystemInfos()
                        .FirstOrDefault(item => string.Equals(item.Name, segment, StringComparison.OrdinalIgnoreCase));
                }

                if (entry == null)
                {
                    builder.Append(segment);
                    resolvable = false;
                    continue;
                }

                builder.Append(entry.Name);
                current = entry as DirectoryInfo ?? new DirectoryInfo(entry.FullName);
            }

            return builder.ToString();
        }
        catch (Exception)
        {
            return path;
        }
    }
}
