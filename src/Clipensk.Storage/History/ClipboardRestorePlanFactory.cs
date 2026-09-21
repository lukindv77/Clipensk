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

    /// <summary>
    /// Builds a plan that publishes only a single plain-text representation of the entry, discarding
    /// every other captured format — the "paste as plain text" mode from the product decision in
    /// <c>docs/OPEN_QUESTIONS.md</c> §10, independent of what formats were actually captured.
    ///
    /// The candidate text is chosen by the same rule the journal preview uses: the first payload in
    /// stored order whose <see cref="RestorableClipboardPayload.SearchText"/> is non-empty (the plain
    /// text itself for a Text payload, the extracted text for HTML/RTF — see
    /// <c>WindowsClipboardTextSearchTextExtractor</c>), falling back to the raw URL for a Link
    /// payload. A file drop has no text extraction of its own and is never a candidate here.
    ///
    /// Every other payload in the entry is reported as skipped, since this mode deliberately
    /// discards them — unlike <see cref="Create"/>, nothing is republished alongside the chosen text.
    /// An entry with no plain-text representation at all fails closed.
    /// </summary>
    public static ClipboardRestorePlan CreatePlainTextOnly(
        RestorableClipboardEntry entry,
        string plainTextFormatName)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(plainTextFormatName);

        List<RestorableClipboardPayload> ordered =
            [.. entry.Payloads.OrderBy(payload => payload.PayloadOrder)];

        RestorableClipboardPayload? chosen = null;
        string? text = null;
        foreach (RestorableClipboardPayload payload in ordered)
        {
            string? candidate = payload.SearchText;
            if (string.IsNullOrWhiteSpace(candidate) && payload.Kind == ClipboardHistoryPayloadKind.Link)
            {
                candidate = payload.InlineCanonicalText;
            }

            if (!string.IsNullOrWhiteSpace(candidate))
            {
                chosen = payload;
                text = candidate;
                break;
            }
        }

        if (chosen is null || text is null)
        {
            throw new InvalidDataException(
                "This history entry has no plain-text representation to publish.");
        }

        List<string> skipped = [.. ordered
            .Where(payload => payload.PayloadOrder != chosen.PayloadOrder)
            .Select(payload => payload.FormatName)];

        List<ClipboardRestoreItem> items =
        [
            new ClipboardRestoreItem(plainTextFormatName, ClipboardHistoryPayloadKind.Text, text, ExternalFilePath: null),
        ];

        return new ClipboardRestorePlan(items, TextConvertedFormatNames: [], skipped);
    }
}
