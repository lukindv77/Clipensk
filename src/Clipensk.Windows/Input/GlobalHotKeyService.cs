using System.ComponentModel;
using System.Runtime.InteropServices;
using Clipensk.Core.Input;
using Clipensk.Windows.Interop;

namespace Clipensk.Windows.Input;

public sealed class GlobalHotKeyService : IGlobalHotKeyService
{
    private const int HotKeyIdA = 1;
    private const int HotKeyIdB = 2;
    private const uint ModNoRepeat = 0x4000;

    private readonly ResidentMessageWindow _messageWindow;
    private int? _activeHotKeyId;
    private bool _disposed;

    internal GlobalHotKeyService(ResidentMessageWindow messageWindow)
    {
        _messageWindow = messageWindow ?? throw new ArgumentNullException(nameof(messageWindow));
        _messageWindow.HotKeyReceived += OnHotKeyReceived;
    }

    public event EventHandler? Pressed;

    public bool IsRegistered => _activeHotKeyId.HasValue;

    public HotKeyGesture? CurrentGesture { get; private set; }

    public void Register(HotKeyGesture gesture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(gesture);
        gesture.Validate();

        if (IsRegistered && Equals(CurrentGesture, gesture))
        {
            return;
        }

        int candidateId = _activeHotKeyId == HotKeyIdA ? HotKeyIdB : HotKeyIdA;
        uint modifiers = (uint)gesture.Modifiers | ModNoRepeat;

        if (!RegisterHotKey(_messageWindow.Handle, candidateId, modifiers, gesture.VirtualKey))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Не удалось зарегистрировать новую глобальную горячую клавишу Clipensk. Прежняя комбинация сохранена.");
        }

        int? previousId = _activeHotKeyId;
        _activeHotKeyId = candidateId;
        CurrentGesture = gesture;

        if (previousId.HasValue)
        {
            UnregisterHotKey(_messageWindow.Handle, previousId.Value);
        }
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

        UnregisterInternal();
        _messageWindow.HotKeyReceived -= OnHotKeyReceived;
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void OnHotKeyReceived(int hotKeyId)
    {
        if (_activeHotKeyId == hotKeyId)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UnregisterInternal()
    {
        if (!_activeHotKeyId.HasValue)
        {
            CurrentGesture = null;
            return;
        }

        UnregisterHotKey(_messageWindow.Handle, _activeHotKeyId.Value);
        _activeHotKeyId = null;
        CurrentGesture = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
}
