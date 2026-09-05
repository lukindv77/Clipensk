using System.Runtime.InteropServices;
using System.Text;
using Clipensk.Core.Input;
using Microsoft.Win32.SafeHandles;

namespace Clipensk.Windows.Input;

internal sealed class WindowsInvocationApplicationResolver
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ImagePathCapacity = 32768;

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

        return new InvocationApplication(processId, TryGetExecutablePath(processId));
    }

    private static string? TryGetExecutablePath(uint processId)
    {
        using SafeProcessHandle processHandle = OpenProcess(
            ProcessQueryLimitedInformation,
            inheritHandle: false,
            processId);
        if (processHandle.IsInvalid)
        {
            return null;
        }

        var buffer = new StringBuilder(ImagePathCapacity);
        uint size = (uint)buffer.Capacity;
        if (!QueryFullProcessImageName(processHandle, 0, buffer, ref size) || size == 0)
        {
            return null;
        }

        return buffer.ToString(0, checked((int)size));
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle processHandle,
        uint flags,
        StringBuilder executableName,
        ref uint size);
}
