using Clipensk.Core.Clipboard;
using Clipensk.Core.History;

namespace Clipensk.Storage.History;

/// <summary>
/// Builds the <see cref="ClipboardRestorePlan"/> for one verified history entry.
///
/// The plan's data types live in <c>Clipensk.Core</c> so the Windows adapter can publish them
/// without depending on storage; only this decision logic needs the storage-side entry.
/// </summary>
public static class ClipboardRestorePlanFactory
{
    private const string PathSeparator = "\r\n";

    /// <summary>
    /// Builds the plan from an entry already verified by
    /// <see cref="ProtectedClipboardHistoryRestoreService"/>.
    ///
    /// A file drop cannot be republished as files. Clipensk stores only the canonical metadata and
    /// never reads or copies the file contents (<c>docs/REQUIREMENTS.md</c> §19), so the originals
    /// may have been moved or deleted. It is republished as plain text — one full path per line, in
    /// the stored item order.
    ///
    /// That conversion never overwrites genuinely captured text: when the entry already carries a
    /// payload in <paramref name="plainTextFormatName"/>, the file list is reported as skipped
    /// instead, because a clipboard can hold only one plain-text value and the captured one is the
    /// user's own.
    ///
    /// An entry with nothing publishable fails closed rather than replacing the user's current
    /// clipboard with an empty package.
    /// </summary>
    public static ClipboardRestorePlan Create(
        RestorableClipboardEntry entry,
        string plainTextFormatName)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(plainTextFormatName);

        bool hasCapturedPlainText = entry.Payloads.Any(payload =>
            payload.Kind != ClipboardHistoryPayloadKind.StorageItems &&
            string.Equals(payload.FormatName, plainTextFormatName, StringComparison.Ordinal));

        var items = new List<ClipboardRestoreItem>(entry.Payloads.Count);
        var converted = new List<string>();
        var skipped = new List<string>();

        foreach (RestorableClipboardPayload payload in entry.Payloads)
        {
            if (payload.Kind != ClipboardHistoryPayloadKind.StorageItems)
            {
                items.Add(new ClipboardRestoreItem(
                    payload.FormatName,
                    payload.Kind,
                    payload.InlineCanonicalText,
                    payload.ExternalFilePath));
                continue;
            }

            if (hasCapturedPlainText)
            {
                skipped.Add(payload.FormatName);
                continue;
            }

            IReadOnlyList<string> paths = ClipboardStorageItemsCanonicalizer.ReadFullPaths(
                payload.InlineCanonicalText
                    ?? throw new InvalidDataException(
                        "A stored file drop carries no canonical representation to convert."));

            items.Add(new ClipboardRestoreItem(
                plainTextFormatName,
                ClipboardHistoryPayloadKind.Text,
                string.Join(PathSeparator, paths),
                ExternalFilePath: null));
            converted.Add(payload.FormatName);
        }

        if (items.Count == 0)
        {
            throw new InvalidDataException(
                "This history entry has no payload that can be published to the clipboard.");
        }

        return new ClipboardRestorePlan(items, converted, skipped);
    }
}
