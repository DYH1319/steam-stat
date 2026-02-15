using System.Text;

namespace ElectronNet.Helpers;

/// <summary>
/// 日志辅助类：将 Console 输出同时写入日志文件
/// 配合 WinExe 项目类型使用，确保生产环境无控制台窗口但日志不丢失
/// </summary>
public static class ConsoleHelper
{
    private static TextWriter? OriginalOut { get; set; }
    private static TextWriter? OriginalError { get; set; }
    private static TeeTextWriter? TeeOut { get; set; }
    private static TeeTextWriter? TeeError { get; set; }

    /// <summary>
    /// 初始化日志系统，将 Console 输出同时写入日志文件
    /// </summary>
    public static void SetupLogging()
    {
        try
        {
            var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(logDir);
            var logFile = Path.Combine(logDir, $"app-{DateTime.Now:yyyy-MM-dd}.log");
            var fileWriter = new StreamWriter(logFile, append: true, Encoding.UTF8) { AutoFlush = true };

            OriginalOut = Console.Out;
            OriginalError = Console.Error;
            TeeOut = new TeeTextWriter(OriginalOut, fileWriter);
            TeeError = new TeeTextWriter(OriginalError, fileWriter);

            Console.SetOut(TeeOut);
            Console.SetError(TeeError);
        }
        catch
        {
            // 日志初始化失败时静默忽略，不影响应用运行
        }
    }

    /// <summary>
    /// 清理日志系统，关闭文件流并恢复原始 Console 流（避免进程清理时挂起）
    /// </summary>
    public static void CleanupLogging()
    {
        try
        {
            if (OriginalOut != null)
            {
                Console.SetOut(OriginalOut);
                TeeOut?.Dispose();
            }
            if (OriginalError != null)
            {
                Console.SetError(OriginalError);
                TeeError?.Dispose();
            }
        }
        catch
        {
            // 清理失败时静默忽略
        }
    }

    /// <summary>
    /// 双输出 TextWriter：同时写入原始流和日志文件
    /// </summary>
    private class TeeTextWriter(TextWriter original, TextWriter file) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            original.Write(value);
            file.Write(value);
        }

        public override void Write(string? value)
        {
            original.Write(value);
            file.Write(value);
        }

        public override void WriteLine(string? value)
        {
            original.WriteLine(value);
            file.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {value}");
        }

        public override void Flush()
        {
            original.Flush();
            file.Flush();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) file.Dispose();
            base.Dispose(disposing);
        }
    }
}
