using Clipensk.Core.Clipboard;
using Clipensk.Core.History;

namespace Clipensk.Storage.History;

/// <summary>One payload that can actually be published to the Windows clipboard.</summary>
public sealed record ClipboardRestoreItem(
    string FormatName,
    ClipboardHistoryPayloadKind Kind,
    string? InlineCanonicalText,
    string? ExternalFilePath);

/// <summary>
/// What a verified history entry puts back on the clipboard.
///
/// Neither list is a silent filter: the caller is expected to tell the user what was converted and
/// what was left out, because the reasons are storage decisions they cannot see.
/// </summary>
public sealed record ClipboardRestorePlan(
    IReadOnlyList<ClipboardRestoreItem> Items,
    IReadOnlyList<string> TextConvertedFormatNames,
    IReadOnlyList<string> SkippedFormatNames)
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
