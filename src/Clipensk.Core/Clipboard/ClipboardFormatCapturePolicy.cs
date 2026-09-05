namespace Clipensk.Core.Clipboard;

public readonly record struct ClipboardFormatCapturePolicy(
    ClipboardCapturePolicyRule Capture,
    long? MaxBytes = null);
