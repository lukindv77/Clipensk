using System.Runtime.InteropServices;
using Clipensk.Core.Localization;

namespace Clipensk.Windows.Interop;

/// <summary>
/// The tray icon a resident clipboard manager needs so it stays reachable after the main window is
/// hidden, per <c>docs/ARCHITECTURE.md</c>'s "Tray Integration" and the explicit product decision:
/// closing the window with the X always minimizes to tray (already implemented in
/// <c>JournalWindow.OnAppWindowClosing</c>), and only this tray icon's "Завершить работу" command
/// actually ends the process. Left-click opens the journal; right-click shows a menu with Журнал,
/// Настройки and Завершить работу, mirroring the classic tray icon convention every Windows user
/// already knows, so no separate UI is needed to teach it.
///
/// Reuses the same <see cref="ResidentMessageWindow"/> the hotkey and clipboard listeners already
/// run on, rather than creating a second hidden window: <c>Shell_NotifyIcon</c> callback messages
/// are just posted to whatever HWND is registered, and this app's message pump (WinUI's own, since
/// the window was created on the UI thread) already dispatches to it.
/// </summary>
public sealed class WindowsTrayIconService : IDisposable
{
    private const uint NimAdd = 0x00000000;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;

    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmNull = 0x0000;

    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;

    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmNonotify = 0x0080;
    private const uint TpmRightButton = 0x0002;

    private const nuint CommandJournal = 1;
    private const nuint CommandSettings = 2;
    private const nuint CommandExit = 3;

    private static readonly nint IdiApplication = 32512;

    private readonly ResidentMessageWindow _messageWindow;
    private readonly ILocalizationService _localization;
    private readonly nint _icon;
    private readonly bool _ownsIcon;
    private bool _added;
    private bool _disposed;

    public WindowsTrayIconService(ResidentMessageWindow messageWindow, ILocalizationService localization)
    {
        _messageWindow = messageWindow ?? throw new ArgumentNullException(nameof(messageWindow));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        (_icon, _ownsIcon) = LoadTrayIcon();
        _messageWindow.TrayIconMessage += OnTrayIconMessage;
    }

    public event Action? JournalRequested;

    public event Action? SettingsRequested;

    public event Action? ExitRequested;

    public void Show()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_added)
        {
            return;
        }

        NotifyIconData data = CreateData();
        data.uFlags = NifMessage | NifIcon | NifTip;
        data.uCallbackMessage = ResidentMessageWindow.WmTrayIconCallback;
        data.hIcon = _icon;
        data.szTip = _localization.GetString("App.Title");

        _added = Shell_NotifyIcon(NimAdd, ref data);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _messageWindow.TrayIconMessage -= OnTrayIconMessage;

        if (_added)
        {
            NotifyIconData data = CreateData();
            Shell_NotifyIcon(NimDelete, ref data);
            _added = false;
        }

        if (_ownsIcon && _icon != 0)
        {
            DestroyIcon(_icon);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// <see cref="UnmanagedType.ByValTStr"/> fields marshal a fixed-size inline buffer, not a
    /// pointer: passing a default (<c>null</c>) C# string for one throws during marshaling, unlike
    /// an ordinary string parameter where <c>null</c> just becomes a null pointer. Every
    /// <see cref="NotifyIconData"/> going into <see cref="Shell_NotifyIcon"/> must start from here.
    /// </summary>
    private NotifyIconData CreateData() => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = _messageWindow.Handle,
        uID = 0,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private void OnTrayIconMessage(uint mouseMessage)
    {
        if (mouseMessage == WmLButtonUp)
        {
            JournalRequested?.Invoke();
            return;
        }

        if (mouseMessage == WmRButtonUp)
        {
            ShowContextMenu();
        }
    }

    private void ShowContextMenu()
    {
        nint menu = CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        try
        {
            AppendMenu(menu, MfString, CommandJournal, _localization.GetString("Tray.Journal"));
            AppendMenu(menu, MfString, CommandSettings, _localization.GetString("Tray.Settings"));
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, CommandExit, _localization.GetString("Tray.Exit"));

            GetCursorPos(out Point cursor);

            // Required for the popup to dismiss correctly when the user clicks away from it; a
            // documented Win32 quirk, not optional cleanup.
            SetForegroundWindow(_messageWindow.Handle);

            int command = TrackPopupMenuEx(
                menu,
                TpmReturnCmd | TpmNonotify | TpmRightButton,
                cursor.X,
                cursor.Y,
                _messageWindow.Handle,
                0);

            // Also required: without this follow-up message, a second click on the tray icon right
            // after dismissing the menu can fail to reopen it.
            PostMessage(_messageWindow.Handle, WmNull, 0, 0);

            switch ((nuint)command)
            {
                case CommandJournal:
                    JournalRequested?.Invoke();
                    break;
                case CommandSettings:
                    SettingsRequested?.Invoke();
                    break;
                case CommandExit:
                    ExitRequested?.Invoke();
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    /// <summary>
    /// Extracts the running executable's own icon so the tray shows whatever the app already
    /// presents elsewhere, rather than this service inventing branding. Falls back to the generic
    /// system application icon if the executable has none embedded (e.g. <c>ApplicationIcon</c> is
    /// unset), which always succeeds, so a tray icon is guaranteed to appear either way.
    /// </summary>
    private static (nint Icon, bool OwnsIcon) LoadTrayIcon()
    {
        string? exePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(exePath))
        {
            var smallIcons = new nint[1];
            uint extracted = ExtractIconEx(exePath, 0, null, smallIcons, 1);
            if (extracted > 0 && smallIcons[0] != 0)
            {
                return (smallIcons[0], true);
            }
        }

        return (LoadIcon(0, IdiApplication), false);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint ExtractIconEx(
        string filePath,
        int iconIndex,
        nint[]? largeIcons,
        nint[]? smallIcons,
        uint iconCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint LoadIcon(nint instance, nint iconResource);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(nint menu, uint flags, nuint id, string? text);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll", EntryPoint = "TrackPopupMenuEx", SetLastError = true)]
    private static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint window, nint lpTpm);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
}
