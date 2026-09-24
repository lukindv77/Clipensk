using Clipensk.Core.Applications;
using Clipensk.Core.Clipboard;
using Clipensk.Core.Input;
using Clipensk.Core.Localization;
using Clipensk.Core.Storage;
using Clipensk.Windows.Clipboard;
using Clipensk.Windows.Input;
using Clipensk.Windows.Interop;

namespace Clipensk.Windows;

public sealed class ResidentWindowsHost : IDisposable, IClipboardAcceptedCaptureDeliveryFactory
{
    private readonly ResidentMessageWindow _messageWindow;
    private readonly GlobalHotKeyService _hotKeyService;
    private readonly ClipboardUpdateMonitor _clipboardMonitor;
    private readonly WindowsTrayIconService _trayIconService;
    private readonly ClipboardStaThread _clipboardThread;
    private bool _disposed;

    public ResidentWindowsHost(
        IClipboardHtmlSearchTextConverter htmlSearchTextConverter,
        IClipboardRtfSearchTextConverter rtfSearchTextConverter,
        ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(htmlSearchTextConverter);
        ArgumentNullException.ThrowIfNull(rtfSearchTextConverter);
        ArgumentNullException.ThrowIfNull(localization);

        _messageWindow = new ResidentMessageWindow();
        _clipboardThread = new ClipboardStaThread();
        _trayIconService = new WindowsTrayIconService(_messageWindow, localization);
        CaptureQueue = new ClipboardCaptureQueue();
        CaptureSourceStage = new ClipboardCaptureSourceStage(
            CaptureQueue,
            new WindowsClipboardSourceApplicationResolver());
        FormatDiscoveryStage = new ClipboardFormatDiscoveryStage(
            new WindowsClipboardFormatSnapshotReader(_clipboardThread));
        FormatSelectionStage = new ClipboardFormatSelectionStage();
        TextContentReader = new WindowsClipboardTextContentReader(_clipboardThread);
        PngImageContentReader = new WindowsClipboardPngImageContentReader(_clipboardThread);
        LinkContentReader = new WindowsClipboardLinkContentReader(_clipboardThread);
        StorageItemsContentReader = new WindowsClipboardStorageItemsContentReader(_clipboardThread);
        CustomBinaryContentReader = new WindowsClipboardCustomBinaryContentReader(_clipboardThread);
        TextSearchTextExtractor = new WindowsClipboardTextSearchTextExtractor(
            htmlSearchTextConverter,
            rtfSearchTextConverter);
        ContentReaderRouter = new ClipboardContentReaderRouter(
            TextContentReader,
            PngImageContentReader,
            LinkContentReader,
            StorageItemsContentReader,
            CustomBinaryContentReader);
        ContentReadPlanStage = new ClipboardContentReadPlanStage(ContentReaderRouter);
        ContentReadExecutionStage = new ClipboardContentReadExecutionStage(
            TextContentReader,
            PngImageContentReader,
            LinkContentReader,
            StorageItemsContentReader,
            CustomBinaryContentReader,
            TextSearchTextExtractor);
        _hotKeyService = new GlobalHotKeyService(_messageWindow);
        _clipboardMonitor = new ClipboardUpdateMonitor(_messageWindow, CaptureQueue);
        RestoreWriter = new WindowsClipboardRestoreWriter(_clipboardMonitor);
    }

    public IGlobalHotKeyService HotKeyService => _hotKeyService;

    public WindowsTrayIconService TrayIconService => _trayIconService;

    /// <summary>
    /// Raised when a second Clipensk instance of the same Windows user handed its launch over to
    /// this one instead of starting a competing resident runtime.
    /// </summary>
    public event Action? ActivationRequested
    {
        add => _messageWindow.ActivationRequested += value;
        remove => _messageWindow.ActivationRequested -= value;
    }

    public ClipboardCaptureQueue CaptureQueue { get; }

    public ClipboardCaptureSourceStage CaptureSourceStage { get; }

    public ClipboardFormatDiscoveryStage FormatDiscoveryStage { get; }

    public ClipboardFormatSelectionStage FormatSelectionStage { get; }

    public IClipboardTextContentReader TextContentReader { get; }

    public IClipboardPngImageContentReader PngImageContentReader { get; }

    public IClipboardLinkContentReader LinkContentReader { get; }

    public IClipboardStorageItemsContentReader StorageItemsContentReader { get; }

    public IClipboardCustomBinaryContentReader CustomBinaryContentReader { get; }

    public IClipboardTextSearchTextExtractor TextSearchTextExtractor { get; }

    public ClipboardContentReaderRouter ContentReaderRouter { get; }

    public ClipboardContentReadPlanStage ContentReadPlanStage { get; }

    public ClipboardContentReadExecutionStage ContentReadExecutionStage { get; }

    public bool IsClipboardMonitoring => _clipboardMonitor.IsStarted;

    /// <summary>
    /// Republishes a stored history entry to the clipboard on this host's message-window thread,
    /// suppressing the capture of Clipensk's own write.
    /// </summary>
    public WindowsClipboardRestoreWriter RestoreWriter { get; }

    public ClipboardCapturePipeline CreateCapturePipeline(IClipboardCapturePolicyProvider policyProvider)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(policyProvider);

        return CreateCapturePipelineCore(
            policyProvider,
            identityRegistry: null,
            discoveredFormatObserver: null);
    }

    public ClipboardCapturePipeline CreateCapturePipeline(
        IClipboardCapturePolicyProvider policyProvider,
        IApplicationIdentityRegistry identityRegistry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(policyProvider);
        ArgumentNullException.ThrowIfNull(identityRegistry);

        return CreateCapturePipelineCore(
            policyProvider,
            identityRegistry,
            discoveredFormatObserver: null);
    }

    public ClipboardCapturePipeline CreateCapturePipeline(
        IClipboardCapturePolicyProvider policyProvider,
        IApplicationIdentityRegistry identityRegistry,
        IApplicationDiscoveredFormatObserver discoveredFormatObserver)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(policyProvider);
        ArgumentNullException.ThrowIfNull(identityRegistry);
        ArgumentNullException.ThrowIfNull(discoveredFormatObserver);

        return CreateCapturePipelineCore(
            policyProvider,
            identityRegistry,
            discoveredFormatObserver);
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

    public ClipboardCaptureReadPlanningPipeline CreateCaptureReadPlanningPipeline(
        IClipboardCapturePolicyProvider policyProvider,
        IApplicationIdentityRegistry identityRegistry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(policyProvider);
        ArgumentNullException.ThrowIfNull(identityRegistry);

        return new ClipboardCaptureReadPlanningPipeline(
            CreateCapturePipeline(policyProvider, identityRegistry),
            ContentReadPlanStage);
    }

    public ClipboardCaptureReadPlanningPipeline CreateCaptureReadPlanningPipeline(
        IClipboardCapturePolicyProvider policyProvider,
        IApplicationIdentityRegistry identityRegistry,
        IApplicationDiscoveredFormatObserver discoveredFormatObserver)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(policyProvider);
        ArgumentNullException.ThrowIfNull(identityRegistry);
        ArgumentNullException.ThrowIfNull(discoveredFormatObserver);

        return new ClipboardCaptureReadPlanningPipeline(
            CreateCapturePipeline(
                policyProvider,
                identityRegistry,
                discoveredFormatObserver),
            ContentReadPlanStage);
    }

    public ClipboardCaptureReadExecutionPipeline CreateCaptureReadExecutionPipeline(
        IClipboardCapturePolicyProvider policyProvider)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(policyProvider);

        return new ClipboardCaptureReadExecutionPipeline(
            CreateCaptureReadPlanningPipeline(policyProvider),
            ContentReadExecutionStage);
    }

    public ClipboardCaptureReadExecutionPipeline CreateCaptureReadExecutionPipeline(
        IClipboardCapturePolicyProvider policyProvider,
        IApplicationIdentityRegistry identityRegistry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(policyProvider);
        ArgumentNullException.ThrowIfNull(identityRegistry);

        return new ClipboardCaptureReadExecutionPipeline(
            CreateCaptureReadPlanningPipeline(policyProvider, identityRegistry),
            ContentReadExecutionStage);
    }

    public ClipboardCaptureReadExecutionPipeline CreateCaptureReadExecutionPipeline(
        IClipboardCapturePolicyProvider policyProvider,
        IApplicationIdentityRegistry identityRegistry,
        IApplicationDiscoveredFormatObserver discoveredFormatObserver)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(policyProvider);
        ArgumentNullException.ThrowIfNull(identityRegistry);
        ArgumentNullException.ThrowIfNull(discoveredFormatObserver);

        return new ClipboardCaptureReadExecutionPipeline(
            CreateCaptureReadPlanningPipeline(
                policyProvider,
                identityRegistry,
                discoveredFormatObserver),
            ContentReadExecutionStage);
    }

    public ClipboardAcceptedCaptureDeliveryPipeline CreateAcceptedCaptureDeliveryPipeline(
        IClipboardCapturePolicyProvider policyProvider,
        IClipboardAcceptedCaptureSink sink)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(policyProvider);
        ArgumentNullException.ThrowIfNull(sink);

        return new ClipboardAcceptedCaptureDeliveryPipeline(
            CreateCaptureReadExecutionPipeline(policyProvider),
            new ClipboardAcceptedCaptureSinkStage(
                new ClipboardAcceptedCaptureStage(),
                sink));
    }

    public ClipboardAcceptedCaptureDeliveryPipeline CreateAcceptedCaptureDeliveryPipeline(
        IClipboardCapturePolicyProvider policyProvider,
        IClipboardAcceptedCaptureSink sink,
        IApplicationIdentityRegistry identityRegistry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(policyProvider);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(identityRegistry);

        return new ClipboardAcceptedCaptureDeliveryPipeline(
            CreateCaptureReadExecutionPipeline(policyProvider, identityRegistry),
            new ClipboardAcceptedCaptureSinkStage(
                new ClipboardAcceptedCaptureStage(),
                sink));
    }

    public ClipboardAcceptedCaptureDeliveryPipeline CreateAcceptedCaptureDeliveryPipeline(
        IClipboardCapturePolicyProvider policyProvider,
        IClipboardAcceptedCaptureSink sink,
        IApplicationIdentityRegistry identityRegistry,
        IApplicationDiscoveredFormatObserver discoveredFormatObserver)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(policyProvider);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(identityRegistry);
        ArgumentNullException.ThrowIfNull(discoveredFormatObserver);

        return new ClipboardAcceptedCaptureDeliveryPipeline(
            CreateCaptureReadExecutionPipeline(
                policyProvider,
                identityRegistry,
                discoveredFormatObserver),
            new ClipboardAcceptedCaptureSinkStage(
                new ClipboardAcceptedCaptureStage(),
                sink));
    }

    IClipboardAcceptedCaptureDelivery IClipboardAcceptedCaptureDeliveryFactory.Create(
        IClipboardCapturePolicyProvider policyProvider,
        IClipboardAcceptedCaptureSink sink,
        IApplicationIdentityRegistry identityRegistry,
        IApplicationDiscoveredFormatObserver discoveredFormatObserver)
    {
        return CreateAcceptedCaptureDeliveryPipeline(
            policyProvider,
            sink,
            identityRegistry,
            discoveredFormatObserver);
    }

    public IClipboardAcceptedCaptureDelivery CreateProtectedAcceptedCaptureDelivery(
        IClipboardCapturePolicyProvider policyProvider,
        IClipboardAcceptedCaptureSink sink,
        ProtectedStorageSessionLease session)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(policyProvider);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(session);

        return new ProtectedClipboardAcceptedCaptureDelivery(
            CreateAcceptedCaptureDeliveryPipeline(policyProvider, sink),
            session);
    }

    public IClipboardAcceptedCaptureDelivery CreateProtectedAcceptedCaptureDelivery(
        IClipboardCapturePolicyProvider policyProvider,
        IClipboardAcceptedCaptureSink sink,
        ProtectedStorageSessionLease session,
        IApplicationIdentityRegistry identityRegistry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(policyProvider);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(identityRegistry);

        return new ProtectedClipboardAcceptedCaptureDelivery(
            CreateAcceptedCaptureDeliveryPipeline(policyProvider, sink, identityRegistry),
            session);
    }

    public IClipboardAcceptedCaptureDelivery CreateProtectedGlobalOnlyAcceptedCaptureDelivery(
        ClipboardCapturePolicy globalPolicy,
        IClipboardAcceptedCaptureSink sink,
        ProtectedStorageSessionLease session)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(globalPolicy);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(session);

        return CreateProtectedAcceptedCaptureDelivery(
            new RepositoryClipboardCapturePolicyProvider(
                new GlobalOnlyClipboardCapturePolicyRepository(globalPolicy)),
            sink,
            session);
    }

    public IClipboardAcceptedCaptureDelivery CreateProtectedGlobalOnlyAcceptedCaptureDelivery(
        ClipboardCapturePolicy globalPolicy,
        IClipboardAcceptedCaptureSink sink,
        ProtectedStorageSessionLease session,
        IApplicationIdentityRegistry identityRegistry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(globalPolicy);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(identityRegistry);

        return CreateProtectedAcceptedCaptureDelivery(
            new RepositoryClipboardCapturePolicyProvider(
                new GlobalOnlyClipboardCapturePolicyRepository(globalPolicy)),
            sink,
            session,
            identityRegistry);
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
        _trayIconService.Dispose();
        _messageWindow.Dispose();
        _clipboardThread.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private ClipboardCapturePipeline CreateCapturePipelineCore(
        IClipboardCapturePolicyProvider policyProvider,
        IApplicationIdentityRegistry? identityRegistry,
        IApplicationDiscoveredFormatObserver? discoveredFormatObserver)
    {
        var policyStage = new ClipboardCapturePolicyResolutionStage(
            policyProvider,
            new ClipboardCapturePolicyEvaluator());

        if (identityRegistry is null)
        {
            return new ClipboardCapturePipeline(
                CaptureSourceStage,
                policyStage,
                FormatDiscoveryStage,
                FormatSelectionStage);
        }

        var identityStage = new ClipboardCaptureApplicationIdentityStage(identityRegistry);
        return discoveredFormatObserver is null
            ? new ClipboardCapturePipeline(
                CaptureSourceStage,
                identityStage,
                policyStage,
                FormatDiscoveryStage,
                FormatSelectionStage)
            : new ClipboardCapturePipeline(
                CaptureSourceStage,
                identityStage,
                policyStage,
                FormatDiscoveryStage,
                new ClipboardApplicationDiscoveredFormatObservationStage(discoveredFormatObserver),
                FormatSelectionStage);
    }
}

