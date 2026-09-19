using Clipensk.Core.History;

namespace Clipensk.Storage.History;

/// <summary>One payload that can actually be published to the Windows clipboard.</summary>
public sealed record ClipboardRestoreItem(
    string FormatName,
    ClipboardHistoryPayloadKind Kind,
    string? InlineCanonicalText,
    string? ExternalFilePath);

/// <summary>
/// What a verified history entry can and cannot put back on the clipboard.
///
/// <see cref="SkippedFormatNames"/> is not a silent filter: the caller is expected to tell the user
/// which formats were left out, because the reason is a storage decision they cannot see.
/// </summary>
public sealed record ClipboardRestorePlan(
    IReadOnlyList<ClipboardRestoreItem> Items,
    IReadOnlyList<string> SkippedFormatNames)
{
    /// <summary>
    /// Builds the plan from an entry already verified by
    /// <see cref="ProtectedClipboardHistoryRestoreService"/>.
    ///
    /// <see cref="ClipboardHistoryPayloadKind.StorageItems"/> is skipped. Clipensk stores only the
    /// canonical metadata of a file drop and never reads or copies the file contents
    /// (<c>docs/REQUIREMENTS.md</c> §19), so the original files may have been moved or deleted and
    /// there is nothing durable to republish. Handing back a file list that may no longer resolve
    /// would be worse than saying it was skipped.
    ///
    /// An entry with nothing publishable fails closed rather than replacing the user's current
    /// clipboard with an empty package.
    /// </summary>
    public static ClipboardRestorePlan Create(RestorableClipboardEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var items = new List<ClipboardRestoreItem>(entry.Payloads.Count);
        var skipped = new List<string>();

        foreach (RestorableClipboardPayload payload in entry.Payloads)
        {
            if (payload.Kind == ClipboardHistoryPayloadKind.StorageItems)
            {
                skipped.Add(payload.FormatName);
                continue;
            }

            items.Add(new ClipboardRestoreItem(
                payload.FormatName,
                payload.Kind,
                payload.InlineCanonicalText,
                payload.ExternalFilePath));
        }

        if (items.Count == 0)
        {
            throw new InvalidDataException(
                "This history entry has no payload that can be published to the clipboard.");
        }

        return new ClipboardRestorePlan(items, skipped);
    }
}
