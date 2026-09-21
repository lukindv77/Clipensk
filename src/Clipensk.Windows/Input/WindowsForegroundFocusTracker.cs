using System.Runtime.InteropServices;

namespace Clipensk.Windows.Input;

/// <summary>
/// Captures and restores the OS foreground window around the journal window's lifecycle, per the
/// product decision in <c>docs/OPEN_QUESTIONS.md</c> §10: clean focus restoration without
/// auto-paste. The handle this returns is meant to live only in memory for one journal session —
/// callers must never persist it — and a failed restore (the captured window closed, or Windows
/// refuses the foreground request) is silently ignored: there is no user-facing error path for this
/// by design.
/// </summary>
public static class WindowsForegroundFocusTracker
{
    public static nint? CaptureForeground()
    {
        nint handle = GetForegroundWindow();
        return handle == 0 ? null : handle;
    }

    public static void TryRestoreForeground(nint? handle)
    {
        if (handle is not { } target || !IsWindow(target))
        {
            return;
        }

        SetForegroundWindow(target);
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);
}
