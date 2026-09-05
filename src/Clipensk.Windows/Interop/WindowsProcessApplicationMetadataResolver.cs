using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Clipensk.Windows.Interop;

internal static class WindowsProcessApplicationMetadataResolver
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;
    private const int ImagePathCapacity = 32768;

    public static (string? ExecutablePath, string? ApplicationUserModelId) Resolve(uint processId)
    {
        using SafeProcessHandle processHandle = OpenProcess(
            ProcessQueryLimitedInformation,
            inheritHandle: false,
            processId);
        if (processHandle.IsInvalid)
        {
            return default;
        }

        return (
            TryGetExecutablePath(processHandle),
            TryGetApplicationUserModelId(processHandle));
    }

    private static string? TryGetExecutablePath(SafeProcessHandle processHandle)
    {
        var buffer = new StringBuilder(ImagePathCapacity);
        uint size = (uint)buffer.Capacity;
        if (!QueryFullProcessImageName(processHandle, 0, buffer, ref size) || size == 0)
        {
            return null;
        }

        return buffer.ToString(0, checked((int)size));
    }

    private static string? TryGetApplicationUserModelId(SafeProcessHandle processHandle)
    {
        uint length = 0;
        int result = GetApplicationUserModelId(
            processHandle,
            ref length,
            applicationUserModelId: null);
        if (result != ErrorInsufficientBuffer || length <= 1)
        {
            return null;
        }

        var buffer = new StringBuilder(checked((int)length));
        result = GetApplicationUserModelId(processHandle, ref length, buffer);
        if (result != ErrorSuccess || length <= 1)
        {
            return null;
        }

        return buffer.ToString();
    }

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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(
        SafeProcessHandle processHandle,
        ref uint applicationUserModelIdLength,
        StringBuilder? applicationUserModelId);
}
