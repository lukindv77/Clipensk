using System.Runtime.InteropServices;

namespace Clipensk.Windows.Interop;

/// <summary>An error shown before any Clipensk window exists, when Clipensk cannot start at all.</summary>
public static class WindowsStartupMessage
{
    private const uint MbOk = 0x00000000;
    private const uint MbIconError = 0x00000010;

    public static void ShowError(string message, string title)
    {
        MessageBox(0, message, title, MbOk | MbIconError);
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint owner, string text, string caption, uint type);
}
