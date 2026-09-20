using System.Runtime.InteropServices;

namespace Clipensk.Windows.Security;

/// <summary>
/// Reads how long the current Windows session has had no keyboard/mouse input, via
/// <c>GetLastInputInfo</c>. This is system-wide input across all applications, not activity inside
/// Clipensk's own window — a resident clipboard tool must not lock while the user is actively
/// working elsewhere and simply never touched Clipensk's UI.
/// </summary>
public static class WindowsIdleTimeReader
{
    public static TimeSpan GetIdleDuration()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info))
        {
            throw new InvalidOperationException("GetLastInputInfo failed to report the last input time.");
        }

        // Both GetLastInputInfo's dwTime and Environment.TickCount are 32-bit millisecond tick
        // counts from GetTickCount and wrap at the same ~49.7-day boundary. Subtracting as unsigned
        // 32-bit values is the standard idiom that stays correct across that wraparound;
        // Environment.TickCount64 would not, because dwTime is fixed at 32 bits by the Win32 API.
        uint idleMilliseconds = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(idleMilliseconds);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
}
