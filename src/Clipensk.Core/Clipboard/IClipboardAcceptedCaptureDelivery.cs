namespace Clipensk.Core.Clipboard;

public interface IClipboardAcceptedCaptureDelivery
{
    ValueTask<bool> ProcessNextAsync(CancellationToken cancellationToken = default);
}
