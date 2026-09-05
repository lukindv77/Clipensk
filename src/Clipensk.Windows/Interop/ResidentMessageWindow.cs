using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Clipensk.Windows.Interop;

internal sealed class ResidentMessageWindow : IDisposable
{
    private const int ErrorClassAlreadyExists = 1410;
    private const uint WmHotKey = 0x0312;
    private const uint WmClipboardUpdate = 0x031D;
    private static readonly nint HwndMessage = new(-3);

    private readonly string _className;
    private readonly WindowProcedure _windowProcedure;
    private readonly nint _instance;
    private nint _handle;
    private bool _disposed;

    public ResidentMessageWindow()
    {
        _className = $"Clipensk.ResidentMessageWindow.{Environment.ProcessId}";
        _windowProcedure = WindowProc;
        _instance = GetModuleHandle(null);

        var windowClass = new WindowClass
        {
            WindowProcedure = Marshal.GetFunctionPointerForDelegate(_windowProcedure),
            Instance = _instance,
            ClassName = _className,
        };

        ushort atom = RegisterClass(ref windowClass);
        if (atom == 0)
        {
            int error = Marshal.GetLastWin32Error();
            if (error != ErrorClassAlreadyExists)
            {
                throw new Win32Exception(error, "Не удалось зарегистрировать служебное окно Clipensk.");
            }
        }

        _handle = CreateWindowEx(
            0,
            _className,
            null,
            0,
            0,
            0,
            0,
            0,
            HwndMessage,
            0,
            _instance,
            0);

        if (_handle == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось создать служебное окно Clipensk.");
        }
    }

    public nint Handle => _handle;

    public event Action<int>? HotKeyReceived;

    public event Action? ClipboardUpdated;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_handle != 0)
        {
            DestroyWindow(_handle);
            _handle = 0;
        }

        UnregisterClass(_className, _instance);
        GC.SuppressFinalize(this);
    }

    private nint WindowProc(nint window, uint message, nuint wParam, nint lParam)
    {
        if (message == WmHotKey)
        {
            HotKeyReceived?.Invoke(unchecked((int)wParam));
            return 0;
        }

        if (message == WmClipboardUpdate)
        {
            ClipboardUpdated?.Invoke();
            return 0;
        }

        return DefWindowProc(window, message, wParam, lParam);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Style;
        public nint WindowProcedure;
        public int ClassExtraBytes;
        public int WindowExtraBytes;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint BackgroundBrush;
        public string? MenuName;
        public string ClassName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", EntryPoint = "RegisterClassW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WindowClass windowClass);

    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClass(string className, nint instance);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        uint extendedStyle,
        string className,
        string? windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
}
