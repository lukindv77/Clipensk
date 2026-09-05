namespace Clipensk.Core.Clipboard;

public readonly record struct ClipboardContentReaderRoute(
    ClipboardSelectedFormat SelectedFormat,
    ClipboardContentReaderKind ReaderKind);
