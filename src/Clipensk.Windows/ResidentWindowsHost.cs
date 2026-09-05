using Clipensk.Core.Clipboard;
using Clipensk.Core.Input;
using Clipensk.Windows.Clipboard;
using Clipensk.Windows.Input;
using Clipensk.Windows.Interop;

namespace Clipensk.Windows;

public sealed class ResidentWindowsHost : IDisposable
{
    private readonly ResidentMessageWindow _messageWindow;
    private readonly GlobalHotKeyService _hotKeyService;
    private readonly ClipboardUpdateMonitor _clipboardMonitor;
    private bool _disposed;

    public ResidentWindowsHost()
    {
        _messageWindow = new ResidentMessageWindow();
        CaptureQueue = new ClipboardCaptureQueue();
        CaptureSourceStage = new ClipboardCaptureSourceStage(
            CaptureQueue,
            new WindowsClipboardSourceApplicationResolver());
        FormatDiscoveryStage = new ClipboardFormatDiscoveryStage(
            new WindowsClipboardFormatSnapshotReader());
        FormatSelectionStage = new ClipboardFormatSelectionStage();
        _hotKeyService = new GlobalHotKeyService(_messageWindow);
        _clipboardMonitor = new ClipboardUpdateMonitor(_messageWindow, CaptureQueue);
    }

    public IGlobalHotKeyService HotKeyService => _hotKeyService;

    public ClipboardCaptureQueue CaptureQueue { get; }

    public ClipboardCaptureSourceStage CaptureSourceStage { get; }

    public ClipboardFormatDiscoveryStage FormatDiscoveryStage { get; }

    public ClipboardFormatSelectionStage FormatSelectionStage { get; }

    public bool IsClipboardMonitoring => _clipboardMonitor.IsStarted;

    public void StartClipboardMonitoring()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _clipboardMonitor.Start();
    }

    public void StopClipboardMonitoring()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _clipboardMonitor.Stop();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _clipboardMonitor.Dispose();
        _hotKeyService.Dispose();
        _messageWindow.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
