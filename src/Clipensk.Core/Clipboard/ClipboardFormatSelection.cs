namespace Clipensk.Core.Clipboard;

public sealed record ClipboardFormatSelection
{
    public ClipboardFormatSelection(
        ClipboardFormatSnapshot snapshot,
        IEnumerable<ClipboardSelectedFormat> formats)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(formats);

        Snapshot = snapshot;
        Formats = Array.AsReadOnly(formats.ToArray());
    }

    public ClipboardFormatSnapshot Snapshot { get; }

    public IReadOnlyList<ClipboardSelectedFormat> Formats { get; }
}
