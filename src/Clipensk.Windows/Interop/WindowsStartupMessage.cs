using System.Runtime.InteropServices;

namespace Clipensk.Windows.Interop;

/// <summary>
/// A message shown before any Clipensk window exists: an error when Clipensk cannot start at all, or
/// a warning after which it starts anyway.
/// </summary>
public static class WindowsStartupMessage
{
    private const uint MbOk = 0x00000000;
    private const uint MbIconError = 0x00000010;
    private const uint MbIconWarning = 0x00000030;

    public static void ShowError(string message, string title)
    {
        MessageBox(0, message, title, MbOk | MbIconError);
    }

    public static void ShowWarning(string message, string title)
    {
        MessageBox(0, message, title, MbOk | MbIconWarning);
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint owner, string text, string caption, uint type);
}
