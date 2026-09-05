namespace Clipensk.Core.Clipboard;

public interface IClipboardAcceptedCaptureSink
{
    ValueTask StoreAsync(
        ClipboardAcceptedCapture capture,
        CancellationToken cancellationToken = default);
}
