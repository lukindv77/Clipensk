using Clipensk.Core.History;

namespace Clipensk.Core.Clipboard;

/// <summary>One payload that can actually be published to the Windows clipboard.</summary>
public sealed record ClipboardRestoreItem(
    string FormatName,
    ClipboardHistoryPayloadKind Kind,
    string? InlineCanonicalText,
    string? ExternalFilePath);

/// <summary>
/// What a verified history entry puts back on the clipboard.
///
/// This lives in Core rather than beside the factory that builds it, because the platform adapter
/// that publishes it must not depend on the storage layer.
///
/// Neither list is a silent filter: the caller is expected to tell the user what was converted and
/// what was left out, because the reasons are storage decisions they cannot see.
/// </summary>
public sealed record ClipboardRestorePlan(
    IReadOnlyList<ClipboardRestoreItem> Items,
    IReadOnlyList<string> TextConvertedFormatNames,
    IReadOnlyList<string> SkippedFormatNames);
