using System.Runtime.InteropServices;

namespace Clipensk.Windows.Interop;

/// <summary>
/// Allows exactly one running Clipensk per Windows user, per explicit product decision. A second
/// user account may run its own instance at the same time, against its own settings and its own
/// storage — the guard never blocks that, because it is keyed by a lock file inside the current
/// user's own profile rather than by a machine-wide name.
///
/// The lock is an exclusively-held file handle rather than a named mutex on purpose: a
/// <c>Global\</c> mutex would be the only name visible across terminal-services sessions, but
/// creating one needs a privilege standard users do not always have, while a file lock is
/// unprivileged and still machine-wide. It also self-heals — if a previous instance was killed, the
/// OS drops the handle and a leftover lock file on disk means nothing.
/// </summary>
public sealed class WindowsSingleInstanceGuard : IDisposable
{
    private const uint MbOk = 0x00000000;
    private const uint MbIconInformation = 0x00000040;
    private static readonly nint HwndMessage = new(-3);

    private readonly FileStream _lockFile;
    private bool _disposed;

    private WindowsSingleInstanceGuard(FileStream lockFile)
    {
        _lockFile = lockFile;
    }

    /// <summary>
    /// Takes the per-user lock. Returns <c>false</c> when another instance of this Windows user
    /// already holds it, in which case this process must not start a resident runtime.
    /// </summary>
    public static bool TryAcquire(string lockFilePath, out WindowsSingleInstanceGuard? guard)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockFilePath);
        guard = null;

        try
        {
            string? directory = Path.GetDirectoryName(lockFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var lockFile = new FileStream(
                lockFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);

            guard = new WindowsSingleInstanceGuard(lockFile);
            return true;
        }
        catch (IOException)
        {
            // The expected "already running" path: another process of this user holds the handle.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Asks the instance that already holds the lock to show its journal, and reports whether the
    /// request could be delivered. It fails when that instance runs in a different terminal-services
    /// session, because window handles do not cross sessions — and activating a window on somebody
    /// else's desktop would be meaningless anyway, so the caller tells the user instead.
    /// </summary>
    public static bool TryActivateRunningInstance()
    {
        nint window = FindWindowEx(HwndMessage, 0, ResidentMessageWindow.WindowClassName, null);
        if (window == 0)
        {
            return false;
        }

        // Without this the running instance has no right to take the foreground: only the process
        // that just received the user's input (this one) can hand that right over.
        if (GetWindowThreadProcessId(window, out uint processId) != 0 && processId != 0)
        {
            AllowSetForegroundWindow(processId);
        }

        return PostMessage(window, ResidentMessageWindow.WmActivationRequest, 0, 0);
    }

    public static void ShowAlreadyRunningMessage(string message, string title)
    {
        MessageBox(0, message, title, MbOk | MbIconInformation);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lockFile.Dispose();
        GC.SuppressFinalize(this);
    }

    [DllImport("user32.dll", EntryPoint = "FindWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindWindowEx(nint parent, nint childAfter, string? className, string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint owner, string text, string caption, uint type);
}
