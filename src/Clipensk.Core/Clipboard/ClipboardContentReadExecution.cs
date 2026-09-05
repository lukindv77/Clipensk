namespace Clipensk.Core.Clipboard;

public sealed record ClipboardContentReadExecution
{
    public ClipboardContentReadExecution(
        ClipboardContentReadPlan plan,
        IEnumerable<ClipboardCapturedContent> capturedContent,
        IEnumerable<ClipboardSelectedFormat> sizeRejectedFormats,
        IEnumerable<ClipboardSelectedFormat> deferredFormats)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        ArgumentNullException.ThrowIfNull(capturedContent);
        ArgumentNullException.ThrowIfNull(sizeRejectedFormats);
        ArgumentNullException.ThrowIfNull(deferredFormats);

        CapturedContent = Array.AsReadOnly(capturedContent.ToArray());
        SizeRejectedFormats = Array.AsReadOnly(sizeRejectedFormats.ToArray());
        DeferredFormats = Array.AsReadOnly(deferredFormats.ToArray());
    }

    public ClipboardContentReadPlan Plan { get; }

    public IReadOnlyList<ClipboardCapturedContent> CapturedContent { get; }

    public IReadOnlyList<ClipboardSelectedFormat> SizeRejectedFormats { get; }

    public IReadOnlyList<ClipboardSelectedFormat> DeferredFormats { get; }

    public IReadOnlyList<ClipboardSelectedFormat> UnsupportedFormats => Plan.UnsupportedFormats;
}
