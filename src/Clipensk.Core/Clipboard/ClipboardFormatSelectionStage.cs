namespace Clipensk.Core.Clipboard;

public sealed class ClipboardFormatSelectionStage
{
    public ClipboardFormatSelection Select(ClipboardFormatSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        ClipboardCapturePolicy policy = snapshot.PolicyContext.Policy;
        if (policy.Capture != ClipboardCapturePolicyRule.Allow || snapshot.ContentSnapshot is null)
        {
            return new ClipboardFormatSelection(snapshot, Array.Empty<ClipboardSelectedFormat>());
        }

        var selected = new List<ClipboardSelectedFormat>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string formatName in snapshot.AvailableFormats)
        {
            if (!seen.Add(formatName))
            {
                continue;
            }

            if (!policy.Formats.TryGetValue(formatName, out ClipboardFormatCapturePolicy formatPolicy)
                || formatPolicy.Capture != ClipboardCapturePolicyRule.Allow)
            {
                continue;
            }

            selected.Add(new ClipboardSelectedFormat(formatName, formatPolicy.MaxBytes));
        }

        return new ClipboardFormatSelection(snapshot, selected);
    }
}
