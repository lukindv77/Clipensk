using System.Runtime.InteropServices;
using Clipensk.Core.Clipboard;
using Clipensk.Windows.Interop;

namespace Clipensk.Windows.Clipboard;

internal sealed class WindowsClipboardSourceApplicationResolver : IClipboardSourceApplicationResolver
{
    public ClipboardSourceApplication? TryResolveCurrent()
    {
        nint ownerWindow = GetClipboardOwner();
        if (ownerWindow == 0)
        {
            return null;
        }

        uint threadId = GetWindowThreadProcessId(ownerWindow, out uint processId);
        if (threadId == 0 || processId == 0)
        {
            return null;
        }

        (string? executablePath, string? applicationUserModelId) =
            WindowsProcessApplicationMetadataResolver.Resolve(processId);

        return new ClipboardSourceApplication(
            processId,
            executablePath,
            applicationUserModelId);
    }

    [DllImport("user32.dll")]
    private static extern nint GetClipboardOwner();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
