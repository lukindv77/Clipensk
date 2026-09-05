using System.Runtime.InteropServices;
using Clipensk.Core.Input;
using Clipensk.Windows.Interop;

namespace Clipensk.Windows.Input;

internal sealed class WindowsInvocationApplicationResolver
{
    public InvocationApplication? TryResolveCurrent()
    {
        nint foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == 0)
        {
            return null;
        }

        uint threadId = GetWindowThreadProcessId(foregroundWindow, out uint processId);
        if (threadId == 0 || processId == 0)
        {
            return null;
        }

        (string? executablePath, string? applicationUserModelId) =
            WindowsProcessApplicationMetadataResolver.Resolve(processId);

        return new InvocationApplication(
            processId,
            executablePath,
            applicationUserModelId);
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
