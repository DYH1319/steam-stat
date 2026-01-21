namespace ElectronNet.Helpers;

public static class FileHelper
{
    /// <summary>
    /// 下载资源到文件，自动识别文件扩展名
    /// </summary>
    /// <param name="url">下载资源的 URL 地址</param>
    /// <param name="directoryPath">保存文件的目录绝对路径</param>
    /// <param name="fileName">保存的文件名（不含扩展名）</param>
    /// <returns>文件的绝对路径，下载失败返回 null</returns>
    public static async Task<string?> DownloadFileAsync(string? url, string? directoryPath, string? fileName)
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

            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);

            var response = await httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            // 从 Content-Type 识别文件扩展名
            string extension = GetFileExtensionFromContentType(response.Content.Headers.ContentType?.MediaType);

            // 构建完整的文件路径
            string fullFileName = $"{fileName}{extension}";
            string filePath = Path.Combine(directoryPath, fullFileName);

            // 获取字节数组类型的文件内容
            var fileBytes = await response.Content.ReadAsByteArrayAsync();

            // 保存到文件
            await File.WriteAllBytesAsync(filePath, fileBytes);

            return filePath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 根据 Content-Type 获取文件扩展名
    /// </summary>
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