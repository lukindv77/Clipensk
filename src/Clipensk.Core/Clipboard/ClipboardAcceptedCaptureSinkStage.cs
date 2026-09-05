namespace Clipensk.Core.Clipboard;

public sealed class ClipboardAcceptedCaptureSinkStage
{
    private readonly ClipboardAcceptedCaptureStage _acceptedCaptureStage;
    private readonly IClipboardAcceptedCaptureSink _sink;

    public ClipboardAcceptedCaptureSinkStage(
        ClipboardAcceptedCaptureStage acceptedCaptureStage,
        IClipboardAcceptedCaptureSink sink)
    {
        _acceptedCaptureStage = acceptedCaptureStage
            ?? throw new ArgumentNullException(nameof(acceptedCaptureStage));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    public async ValueTask<bool> StoreAsync(
        ClipboardContentReadExecution execution,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);
        cancellationToken.ThrowIfCancellationRequested();

        ClipboardAcceptedCapture? acceptedCapture = _acceptedCaptureStage.Create(execution);
        if (acceptedCapture is null)
        {
            return false;
        }

        await _sink
            .StoreAsync(acceptedCapture, cancellationToken)
            .ConfigureAwait(false);

        return true;
    }
}
