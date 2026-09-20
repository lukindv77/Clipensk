using Microsoft.Win32;

namespace Clipensk.Windows.Autostart;

/// <summary>
/// Per-user autostart via the registry Run key, per explicit product decision: opt-in through
/// Settings, off by default, current-user only. This deliberately uses <see cref="Registry.CurrentUser"/>
/// rather than <c>HKEY_LOCAL_MACHINE</c> — a machine-wide registration would need elevation this app
/// never requests and would start it for every user on the machine, not just the one who opted in.
/// </summary>
public static class WindowsAutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Clipensk";

    public static bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            throw new InvalidOperationException("Не удалось определить путь к исполняемому файлу Clipensk.");
        }

        key.SetValue(ValueName, $"\"{exePath}\"");
    }
}
