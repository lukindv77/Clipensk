using System.ComponentModel;
using System.Runtime.InteropServices;
using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Clipensk.Windows.Interop;

namespace Clipensk.Windows.Clipboard;

internal sealed class ClipboardUpdateMonitor : IDisposable
{
    private readonly ResidentMessageWindow _messageWindow;
    private readonly ClipboardCaptureQueue _captureQueue;
    private bool _isStarted;
    private bool _disposed;

    public ClipboardUpdateMonitor(
        ResidentMessageWindow messageWindow,
        ClipboardCaptureQueue captureQueue)
    {
        _messageWindow = messageWindow ?? throw new ArgumentNullException(nameof(messageWindow));
        _captureQueue = captureQueue ?? throw new ArgumentNullException(nameof(captureQueue));
        _messageWindow.ClipboardUpdated += OnClipboardUpdated;
    }

    public bool IsStarted => _isStarted;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isStarted)
        {
            return;
        }

        if (!AddClipboardFormatListener(_messageWindow.Handle))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Не удалось подписать служебное окно Clipensk на изменения буфера обмена.");
        }

        _isStarted = true;
    }

    public void Stop()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isStarted)
        {
            return;
        }

        if (!RemoveClipboardFormatListener(_messageWindow.Handle))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Не удалось отключить служебное окно Clipensk от изменений буфера обмена.");
        }

        _isStarted = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_isStarted)
        {
            RemoveClipboardFormatListener(_messageWindow.Handle);
            _isStarted = false;
        }

        _messageWindow.ClipboardUpdated -= OnClipboardUpdated;
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void OnClipboardUpdated()
    {
        _captureQueue.TryEnqueue(new ClipboardCaptureRequest(EventTimeContext.CaptureNow()));
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(nint window);
}
