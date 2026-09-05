namespace Clipensk.Core.Clipboard;

public sealed class ClipboardAcceptedCapture
{
    public ClipboardAcceptedCapture(
        ClipboardCaptureContext captureContext,
        IEnumerable<ClipboardCapturedContent> content)
    {
        ArgumentNullException.ThrowIfNull(content);

        ClipboardCapturedContent[] captured = content.ToArray();
        if (captured.Length == 0)
        {
            throw new ArgumentException(
                "Accepted clipboard capture must contain at least one payload.",
                nameof(content));
        }

        CaptureContext = captureContext;
        Content = Array.AsReadOnly(captured);
    }

    public ClipboardCaptureContext CaptureContext { get; }

    public IReadOnlyList<ClipboardCapturedContent> Content { get; }
}
