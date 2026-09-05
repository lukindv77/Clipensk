namespace Clipensk.Core.Clipboard;

public sealed record ClipboardFormatSnapshot
{
    public ClipboardFormatSnapshot(
        ClipboardCapturePolicyContext policyContext,
        IEnumerable<string> availableFormats)
    {
        ArgumentNullException.ThrowIfNull(availableFormats);

        string[] formats = availableFormats.ToArray();
        if (formats.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Clipboard format name cannot be empty.", nameof(availableFormats));
        }

        PolicyContext = policyContext;
        AvailableFormats = Array.AsReadOnly(formats);
    }

    public ClipboardCapturePolicyContext PolicyContext { get; }

    public IReadOnlyList<string> AvailableFormats { get; }
}
