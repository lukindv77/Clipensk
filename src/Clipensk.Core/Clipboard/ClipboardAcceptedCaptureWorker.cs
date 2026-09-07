namespace Clipensk.Core.Clipboard;

/// <summary>
/// Serially consumes clipboard capture requests through one accepted-delivery graph.
/// A failed request is dropped and the worker continues; cancellation stops the loop normally.
/// </summary>
public sealed class ClipboardAcceptedCaptureWorker
{
    private readonly IClipboardAcceptedCaptureDelivery _delivery;

    public ClipboardAcceptedCaptureWorker(IClipboardAcceptedCaptureDelivery delivery)
    {
        _delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _delivery
                    .ProcessNextAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Production delivery dequeues the request before source/policy/read/persist stages.
                // A failed capture is therefore fail-closed and must not terminate resident capture
                // for later clipboard updates.
            }
        }
    }
}
