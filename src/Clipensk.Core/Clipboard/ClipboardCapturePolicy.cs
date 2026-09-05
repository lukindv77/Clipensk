using System.Collections.ObjectModel;

namespace Clipensk.Core.Clipboard;

public sealed record ClipboardCapturePolicy
{
    private static readonly IReadOnlyDictionary<string, ClipboardFormatCapturePolicy> EmptyFormats =
        new ReadOnlyDictionary<string, ClipboardFormatCapturePolicy>(
            new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal));

    public ClipboardCapturePolicy(
        ClipboardCapturePolicyRule capture,
        IReadOnlyDictionary<string, ClipboardFormatCapturePolicy>? formats = null)
    {
        Capture = capture;

        if (formats is null || formats.Count == 0)
        {
            Formats = EmptyFormats;
            return;
        }

        var copy = new Dictionary<string, ClipboardFormatCapturePolicy>(formats.Count, StringComparer.Ordinal);
        foreach ((string formatName, ClipboardFormatCapturePolicy policy) in formats)
        {
            if (string.IsNullOrWhiteSpace(formatName))
            {
                throw new ArgumentException("Clipboard format name cannot be empty.", nameof(formats));
            }

            if (policy.MaxBytes is <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(formats),
                    "Configured clipboard format size limit must be positive.");
            }

            copy.Add(formatName, policy);
        }

        Formats = new ReadOnlyDictionary<string, ClipboardFormatCapturePolicy>(copy);
    }

    public ClipboardCapturePolicyRule Capture { get; }

    public IReadOnlyDictionary<string, ClipboardFormatCapturePolicy> Formats { get; }
}
