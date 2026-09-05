namespace Clipensk.Core.Clipboard;

public sealed class ClipboardCaptureSourceStage
{
    private readonly ClipboardCaptureQueue _captureQueue;
    private readonly IClipboardSourceApplicationResolver _sourceApplicationResolver;

    public ClipboardCaptureSourceStage(
        ClipboardCaptureQueue captureQueue,
        IClipboardSourceApplicationResolver sourceApplicationResolver)
    {
        _captureQueue = captureQueue ?? throw new ArgumentNullException(nameof(captureQueue));
        _sourceApplicationResolver = sourceApplicationResolver
            ?? throw new ArgumentNullException(nameof(sourceApplicationResolver));
    }

    public async ValueTask<ClipboardCaptureContext> ResolveNextAsync(
        CancellationToken cancellationToken = default)
    {
        ClipboardCaptureRequest request = await _captureQueue
            .DequeueAsync(cancellationToken)
            .ConfigureAwait(false);
        ClipboardSourceApplication? sourceApplication = _sourceApplicationResolver.TryResolveCurrent();

        return new ClipboardCaptureContext(request, sourceApplication);
    }
}
