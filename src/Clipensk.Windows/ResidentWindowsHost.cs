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
        TextContentReader = new WindowsClipboardTextContentReader();
        PngImageContentReader = new WindowsClipboardPngImageContentReader();
        LinkContentReader = new WindowsClipboardLinkContentReader();
        StorageItemsContentReader = new WindowsClipboardStorageItemsContentReader();
        ContentReaderRouter = new ClipboardContentReaderRouter(
            TextContentReader,
            PngImageContentReader,
            LinkContentReader,
            StorageItemsContentReader);
        ContentReadPlanStage = new ClipboardContentReadPlanStage(ContentReaderRouter);
        ContentReadExecutionStage = new ClipboardContentReadExecutionStage(
            TextContentReader,
            PngImageContentReader,
            LinkContentReader,
            StorageItemsContentReader);
        _hotKeyService = new GlobalHotKeyService(_messageWindow);
        _clipboardMonitor = new ClipboardUpdateMonitor(_messageWindow, CaptureQueue);
    }

    public IGlobalHotKeyService HotKeyService => _hotKeyService;

    public ClipboardCaptureQueue CaptureQueue { get; }

    public ClipboardCaptureSourceStage CaptureSourceStage { get; }

    public ClipboardFormatDiscoveryStage FormatDiscoveryStage { get; }

    public ClipboardFormatSelectionStage FormatSelectionStage { get; }

    public IClipboardTextContentReader TextContentReader { get; }

    public IClipboardPngImageContentReader PngImageContentReader { get; }

    public IClipboardLinkContentReader LinkContentReader { get; }

    public IClipboardStorageItemsContentReader StorageItemsContentReader { get; }

    public ClipboardContentReaderRouter ContentReaderRouter { get; }

    public ClipboardContentReadPlanStage ContentReadPlanStage { get; }

    public ClipboardContentReadExecutionStage ContentReadExecutionStage { get; }

    public bool IsClipboardMonitoring => _clipboardMonitor.IsStarted;

    public ClipboardCapturePipeline CreateCapturePipeline(IClipboardCapturePolicyProvider policyProvider)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(policyProvider);

        return new ClipboardCapturePipeline(
            CaptureSourceStage,
            new ClipboardCapturePolicyResolutionStage(
                policyProvider,
                new ClipboardCapturePolicyEvaluator()),
            FormatDiscoveryStage,
            FormatSelectionStage);
    }

    public ClipboardCaptureReadPlanningPipeline CreateCaptureReadPlanningPipeline(
        IClipboardCapturePolicyProvider policyProvider)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(policyProvider);

        return new ClipboardCaptureReadPlanningPipeline(
            CreateCapturePipeline(policyProvider),
            ContentReadPlanStage);
    }

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
