namespace Clipensk.Core.Clipboard;

public readonly record struct ClipboardCapturePolicyContext(
    ClipboardCaptureContext CaptureContext,
    ClipboardCapturePolicy Policy);
