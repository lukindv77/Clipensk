using System.ComponentModel;
using System.Runtime.InteropServices;
using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Clipensk.Windows.Interop;

namespace Clipensk.Windows.Clipboard;

internal sealed class ClipboardUpdateMonitor : IDisposable
{
    private readonly object _gate = new();
    private readonly ResidentMessageWindow _messageWindow;
    private readonly ClipboardCaptureQueue _captureQueue;
    private readonly ClipboardSelfWriteSuppressor _selfWriteSuppressor = new();
    private long _captureEpoch;
    private bool _isListenerRegistered;
    private bool _acceptUpdates;
    private bool _disposed;

    public ClipboardUpdateMonitor(
        ResidentMessageWindow messageWindow,
        ClipboardCaptureQueue captureQueue)
    {
        _messageWindow = messageWindow ?? throw new ArgumentNullException(nameof(messageWindow));
        _captureQueue = captureQueue ?? throw new ArgumentNullException(nameof(captureQueue));
        _messageWindow.ClipboardUpdated += OnClipboardUpdated;
    }

    public bool IsStarted
    {
        get
        {
            lock (_gate)
            {
                return _acceptUpdates;
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_acceptUpdates)
            {
                return;
            }

            if (!_isListenerRegistered)
            {
                if (!AddClipboardFormatListener(_messageWindow.Handle))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Не удалось подписать служебное окно Clipensk на изменения буфера обмена.");
                }

                _isListenerRegistered = true;
            }

            _selfWriteSuppressor.Reset();
            _captureEpoch = _captureQueue.BeginCaptureEpoch();
            _acceptUpdates = true;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_acceptUpdates)
            {
                return;
            }

            _acceptUpdates = false;
            _selfWriteSuppressor.Reset();
            _captureQueue.InvalidateCaptureEpoch(_captureEpoch);

            if (_isListenerRegistered)
            {
                if (!RemoveClipboardFormatListener(_messageWindow.Handle))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Не удалось отключить служебное окно Clipensk от изменений буфера обмена.");
                }

                _isListenerRegistered = false;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _acceptUpdates = false;
            _captureQueue.InvalidateCaptureEpoch(_captureEpoch);

            if (_isListenerRegistered)
            {
                RemoveClipboardFormatListener(_messageWindow.Handle);
                _isListenerRegistered = false;
            }

            _disposed = true;
        }

        _messageWindow.ClipboardUpdated -= OnClipboardUpdated;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Arms suppression of the clipboard state Clipensk itself just published, so republishing a
    /// stored entry is not captured again as a brand-new event. The caller must read the sequence
    /// number and call this on the message-loop thread, without yielding after the write: the
    /// pending <c>WM_CLIPBOARDUPDATE</c> cannot be dispatched until that thread returns to the
    /// loop, so arming stays ahead of the update it is meant for.
    /// </summary>
    public void SuppressSelfWrite(uint sequenceNumber) =>
        _selfWriteSuppressor.SuppressSequenceNumber(sequenceNumber);

    public static uint ReadClipboardSequenceNumber() => GetClipboardSequenceNumber();

    private void OnClipboardUpdated()
    {
        long captureEpoch;
        lock (_gate)
        {
            if (_disposed || !_acceptUpdates)
            {
                return;
            }

            captureEpoch = _captureEpoch;
        }

        if (!_selfWriteSuppressor.ShouldCapture(GetClipboardSequenceNumber()))
        {
            return;
        }

        _captureQueue.TryEnqueue(
            new ClipboardCaptureRequest(EventTimeContext.CaptureNow()),
            captureEpoch);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(nint window);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
