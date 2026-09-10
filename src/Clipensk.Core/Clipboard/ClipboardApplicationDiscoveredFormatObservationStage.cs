using Clipensk.Core.Applications;

namespace Clipensk.Core.Clipboard;

public sealed class ClipboardApplicationDiscoveredFormatObservationStage
{
    private readonly IApplicationDiscoveredFormatObserver _observer;

    public ClipboardApplicationDiscoveredFormatObservationStage(
        IApplicationDiscoveredFormatObserver observer)
    {
        _observer = observer ?? throw new ArgumentNullException(nameof(observer));
    }

    public async ValueTask ObserveAsync(
        ClipboardFormatSnapshot formatSnapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(formatSnapshot);

        if (formatSnapshot.ContentSnapshot is null)
        {
            return;
        }

        ClipboardCaptureContext captureContext = formatSnapshot.PolicyContext.CaptureContext;
        ApplicationId? applicationId = captureContext.SourceApplicationId;
        if (applicationId is null)
        {
            return;
        }

        await _observer.ObserveAsync(
                applicationId,
                formatSnapshot.AvailableFormats,
                captureContext.Request.EventTime.Timestamp.ToUniversalTime(),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
