using System.IO;

namespace QcyH3Tray.App;

/// <summary>
/// 全局异常日志：追加写 <c>%LOCALAPPDATA%\QcyH3Tray\error.log</c>。
/// 任何日志失败都被吞掉（日志永远不影响主流程）。
/// </summary>
internal static class ErrorLog
{
    private static readonly object Gate = new();

    public static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QcyH3Tray");

    public static string LogPath => Path.Combine(LogDirectory, "error.log");

    public static void Write(string context, Exception? exception)
        => Write($"{context}: {exception}");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志写不进去就放弃
        }
    }
}
