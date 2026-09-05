namespace Clipensk.Core.Clipboard;

public readonly record struct ClipboardCaptureContext(
    ClipboardCaptureRequest Request,
    ClipboardSourceApplication? SourceApplication,
    Clipensk.Core.Applications.ApplicationId? SourceApplicationId = null);
