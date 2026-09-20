using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Clipensk.Windows.Interop;

internal sealed class ResidentMessageWindow : IDisposable
{
    private const int ErrorClassAlreadyExists = 1410;
    private const uint WmHotKey = 0x0312;
    private const uint WmClipboardUpdate = 0x031D;
    private static readonly nint HwndMessage = new(-3);

    /// <summary>
    /// The <c>Shell_NotifyIcon</c> callback message this window listens for. Using the classic
    /// (pre-<c>NIM_SETVERSION</c>) contract, Windows posts this with <c>wParam</c> = icon ID and
    /// <c>lParam</c> = the mouse message (e.g. <c>WM_RBUTTONUP</c>) that occurred over the icon.
    /// </summary>
    internal const uint WmTrayIconCallback = 0x8000 + 1; // WM_APP + 1

    /// <summary>
    /// Posted cross-process by a second Clipensk instance of the same Windows user to hand the
    /// request over to the instance that already owns the single-instance lock, instead of starting
    /// a second resident runtime against the same storage.
    /// </summary>
    internal const uint WmActivationRequest = 0x8000 + 2; // WM_APP + 2

    /// <summary>
    /// Fixed so a second instance can locate this window with
    /// <c>FindWindowEx(HWND_MESSAGE, …)</c>. Window classes are per-process, so two processes
    /// registering the same name never collide; the class is also per-session, so a different
    /// Windows user's instance is never found from here.
    /// </summary>
    internal const string WindowClassName = "Clipensk.ResidentMessageWindow";

    private readonly string _className;
    private readonly WindowProcedure _windowProcedure;
    private readonly nint _instance;
    private nint _handle;
    private bool _disposed;

    public ResidentMessageWindow()
    {
        _className = WindowClassName;
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

    /// <summary>Raised with the mouse message (e.g. <c>WM_LBUTTONUP</c>) reported for the tray icon.</summary>
    public event Action<uint>? TrayIconMessage;

    /// <summary>Raised when a second instance of this Windows user asked this one to come forward.</summary>
    public event Action? ActivationRequested;

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

        if (message == WmTrayIconCallback)
        {
            TrayIconMessage?.Invoke(unchecked((uint)lParam));
            return 0;
        }

        if (message == WmActivationRequest)
        {
            ActivationRequested?.Invoke();
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
