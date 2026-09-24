namespace Clipensk.Core.Clipboard;

/// <summary>
/// Serially consumes clipboard capture requests through one accepted-delivery graph.
/// A failed request is dropped and the worker continues; cancellation stops the loop normally.
/// The failure is still reported to <c>captureFailed</c>, so a capture that keeps failing is not
/// lost silently.
/// </summary>
public sealed class ClipboardAcceptedCaptureWorker
{
    private readonly IClipboardAcceptedCaptureDelivery _delivery;
    private readonly Action<Exception>? _captureFailed;

    public ClipboardAcceptedCaptureWorker(
        IClipboardAcceptedCaptureDelivery delivery,
        Action<Exception>? captureFailed = null)
    {
        _delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
        _captureFailed = captureFailed;
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
            catch (Exception exception)
            {
                // Production delivery dequeues the request before source/policy/read/persist stages.
                // A failed capture is therefore fail-closed and must not terminate resident capture
                // for later clipboard updates.
                ReportCaptureFailure(exception);
            }
        }
    }

    private void ReportCaptureFailure(Exception exception)
    {
        try
        {
            _captureFailed?.Invoke(exception);
        }
        catch
        {
            // Reporting is best effort; it must not stop resident capture either.
        }
    }
}
