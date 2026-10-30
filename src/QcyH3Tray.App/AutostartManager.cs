using Microsoft.Win32;

namespace QcyH3Tray.App;

/// <summary>
/// 开机自启：HKCU\Software\Microsoft\Windows\CurrentVersion\Run 下
/// <c>QcyH3Tray</c> 值（带引号的当前 exe 完整路径）。
/// </summary>
internal static class AutostartManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "QcyH3Tray";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string path && path.Trim('"').Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>开启：写入当前 exe 路径；关闭：删除该值（不存在时静默）。</summary>
    /// <exception cref="InvalidOperationException">无法定位当前 exe 或无法写注册表。</exception>
    public static void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            using var existing = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            existing?.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        string exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法获取当前 exe 路径，开机自启设置失败。");
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (key is null)
        {
            throw new InvalidOperationException("无法打开注册表 Run 键，开机自启设置失败。");
        }
        key.SetValue(ValueName, $"\"{exePath}\"");
    }
}
