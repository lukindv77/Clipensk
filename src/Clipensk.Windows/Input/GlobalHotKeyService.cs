using System.ComponentModel;
using System.Runtime.InteropServices;
using Clipensk.Core.Input;
using Clipensk.Windows.Interop;

namespace Clipensk.Windows.Input;

public sealed class GlobalHotKeyService : IGlobalHotKeyService
{
    private const int JournalHotKeyId = 1;
    private const uint ModNoRepeat = 0x4000;

    private readonly ResidentMessageWindow _messageWindow;
    private bool _disposed;

    public GlobalHotKeyService()
    {
        _messageWindow = new ResidentMessageWindow();
        _messageWindow.HotKeyReceived += OnHotKeyReceived;
    }

    public event EventHandler? Pressed;

    public bool IsRegistered { get; private set; }

    public HotKeyGesture? CurrentGesture { get; private set; }

    public void Register(HotKeyGesture gesture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(gesture);
        gesture.Validate();

        HotKeyGesture? previousGesture = CurrentGesture;
        bool hadPreviousRegistration = IsRegistered;

        if (hadPreviousRegistration)
        {
            UnregisterInternal();
        }

        uint modifiers = (uint)gesture.Modifiers | ModNoRepeat;
        if (!RegisterHotKey(_messageWindow.Handle, JournalHotKeyId, modifiers, gesture.VirtualKey))
        {
            int error = Marshal.GetLastWin32Error();

            if (hadPreviousRegistration && previousGesture is not null)
            {
                TryRestore(previousGesture);
            }

            throw new Win32Exception(error, "Не удалось зарегистрировать глобальную горячую клавишу Clipensk.");
        }

        CurrentGesture = gesture;
        IsRegistered = true;
    }

    public void Unregister()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        UnregisterInternal();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (IsRegistered)
        {
            UnregisterInternal();
        }

        _messageWindow.HotKeyReceived -= OnHotKeyReceived;
        _messageWindow.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void OnHotKeyReceived(int hotKeyId)
    {
        if (hotKeyId == JournalHotKeyId)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UnregisterInternal()
    {
        if (!IsRegistered)
        {
            return;
        }

        UnregisterHotKey(_messageWindow.Handle, JournalHotKeyId);
        IsRegistered = false;
        CurrentGesture = null;
    }

    private void TryRestore(HotKeyGesture gesture)
    {
        uint modifiers = (uint)gesture.Modifiers | ModNoRepeat;
        if (RegisterHotKey(_messageWindow.Handle, JournalHotKeyId, modifiers, gesture.VirtualKey))
        {
            CurrentGesture = gesture;
            IsRegistered = true;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
}
