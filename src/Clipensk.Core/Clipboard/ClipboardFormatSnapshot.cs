namespace Clipensk.Core.Clipboard;

public sealed record ClipboardFormatSnapshot
{
    private static readonly IReadOnlyList<string> EmptyFormats = Array.Empty<string>();

    public ClipboardFormatSnapshot(
        ClipboardCapturePolicyContext policyContext,
        IClipboardContentSnapshot? contentSnapshot)
    {
        PolicyContext = policyContext;
        ContentSnapshot = contentSnapshot;
    }

    public ClipboardCapturePolicyContext PolicyContext { get; }

    public IClipboardContentSnapshot? ContentSnapshot { get; }

    public IReadOnlyList<string> AvailableFormats => ContentSnapshot?.AvailableFormats ?? EmptyFormats;
}
