namespace Clipensk.Core.Clipboard;

public readonly record struct ClipboardSelectedFormat(
    string FormatName,
    long? MaxBytes);
