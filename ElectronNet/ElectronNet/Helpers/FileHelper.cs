using Microsoft.Extensions.Logging;
using SteamStat.Core.Http;

namespace ElectronNet.Helpers;

public sealed class FileHelper(
    IHttpClientFactory httpClientFactory,
    SteamAccessOptions accessOptions,
    ILogger<FileHelper> logger)
{
    /// <summary>
    /// 下载资源到文件，自动识别文件扩展名
    /// </summary>
    /// <param name="url">下载资源的 URL 地址</param>
    /// <param name="directoryPath">保存文件的目录绝对路径</param>
    /// <param name="fileName">保存的文件名（不含扩展名）</param>
    /// <returns>文件的绝对路径；入参无效返回 null；下载失败返回 <see cref="string.Empty"/>（调用方据此保留原有值）</returns>
    public async Task<string?> DownloadFileAsync(string? url, string? directoryPath, string? fileName, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(directoryPath) || string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            // 确保目录存在
            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            var uri = new Uri(url);
            var clientName = IsSteamCdn(uri) ? SteamStatHttpClients.SteamCdn : SteamStatHttpClients.Download;
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            SteamHttpRequestOptions.SetOperation(request, "asset-download");
            using var response = await httpClientFactory.CreateClient(clientName).SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            // 从 Content-Type 识别文件扩展名
            string extension = GetFileExtensionFromContentType(response.Content.Headers.ContentType?.MediaType);
            if (extension == ".bin") throw new InvalidDataException("Unsupported download content type.");
            if (response.Content.Headers.ContentLength > accessOptions.MaximumDownloadBytes)
                throw new InvalidDataException("Download exceeds the configured size limit.");

            // 构建完整的文件路径
            string fullFileName = $"{fileName}{extension}";
            string filePath = Path.Combine(directoryPath, fullFileName);

            // 获取字节数组类型的文件内容
            var fileBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (fileBytes.Length > accessOptions.MaximumDownloadBytes)
                throw new InvalidDataException("Download exceeds the configured size limit.");

            // 保存到文件
            await WriteAtomicallyAsync(filePath, fileBytes, cancellationToken);

            return filePath;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Failed to download file {FileName} to {DirectoryPath} with {ExceptionType}",
                fileName, directoryPath, exception.GetType().Name);
            return string.Empty;
        }
    }

    /// <summary>
    /// 根据 Content-Type 获取文件扩展名
    /// </summary>
    private static async Task WriteAtomicallyAsync(
        string filePath,
        byte[] content,
        CancellationToken cancellationToken)
    {
        var temporaryPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, content, cancellationToken);
            File.Move(temporaryPath, filePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static bool IsSteamCdn(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttps
           && (uri.Host.Equals("avatars.akamai.steamstatic.com", StringComparison.OrdinalIgnoreCase)
               || uri.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase));

    private static string GetFileExtensionFromContentType(string? mediaType)
    {
        if (string.IsNullOrEmpty(mediaType))
            return ".bin";

        // 常见的 MIME 类型映射
        return mediaType.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/svg+xml" => ".svg",
            "image/bmp" => ".bmp",
            "image/x-icon" => ".ico",
            "video/mp4" => ".mp4",
            "video/webm" => ".webm",
            "audio/mpeg" => ".mp3",
            "audio/wav" => ".wav",
            "application/json" => ".json",
            "application/pdf" => ".pdf",
            "text/plain" => ".txt",
            "text/html" => ".html",
            _ => ".bin"
        };
    }
}
