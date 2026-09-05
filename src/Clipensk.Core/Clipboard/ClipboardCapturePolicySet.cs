namespace Clipensk.Core.Clipboard;

public sealed record ClipboardCapturePolicySet
{
    public ClipboardCapturePolicySet(
        ClipboardCapturePolicy globalPolicy,
        ClipboardCapturePolicy? applicationPolicy = null)
    {
        GlobalPolicy = globalPolicy ?? throw new ArgumentNullException(nameof(globalPolicy));
        ApplicationPolicy = applicationPolicy;
    }

    public ClipboardCapturePolicy GlobalPolicy { get; }

    public ClipboardCapturePolicy? ApplicationPolicy { get; }
}
