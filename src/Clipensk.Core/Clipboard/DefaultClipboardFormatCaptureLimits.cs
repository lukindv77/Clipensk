namespace Clipensk.Core.Clipboard;

/// <summary>
/// Recommended default size limits, in bytes, for the standard clipboard formats enabled by
/// default on first-run setup, per the product decision in <c>docs/OPEN_QUESTIONS.md</c> §6. These
/// are only a starting suggestion the setup screen pre-fills — the user can change or disable any
/// of them before saving, and nothing enforces these exact numbers afterward.
///
/// Custom binary formats have no default limit here: the decision enables the mechanism, not any
/// specific format, and the whitelist stays empty until the user names an exact format and file
/// extension — at which point they set its limit on the same screen. WebLink and ApplicationLink
/// are not part of the 2026-09-20 decision and keep requiring an explicit choice, exactly as before.
/// </summary>
public static class DefaultClipboardFormatCaptureLimits
{
    /// <summary>Plain/Unicode text — UTF-8 bytes of the copied text itself.</summary>
    public const long PlainTextMaxBytes = 2L * 1024 * 1024;

    /// <summary>HTML — UTF-8 bytes of the raw <c>CF_HTML</c> markup, not the visible text alone.</summary>
    public const long HtmlMaxBytes = 8L * 1024 * 1024;

    /// <summary>RTF — UTF-8 bytes of the raw RTF, kept below HTML's limit since embedded images are
    /// hex-encoded (roughly doubling their size) and are already captured separately as PNG.</summary>
    public const long RtfMaxBytes = 5L * 1024 * 1024;

    /// <summary>Images — bytes of the normalized PNG, after decoding and re-encoding.</summary>
    public const long ImageMaxBytes = 15L * 1024 * 1024;

    /// <summary><c>CF_HDROP</c>/StorageItems — UTF-8 bytes of the canonical file-list metadata JSON,
    /// not the size of the referenced files.</summary>
    public const long StorageItemsMaxBytes = 2L * 1024 * 1024;
}
