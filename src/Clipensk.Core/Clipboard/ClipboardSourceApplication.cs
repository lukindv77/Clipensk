namespace Clipensk.Core.Clipboard;

public readonly record struct ClipboardSourceApplication(
    uint ProcessId,
    string? ExecutablePath,
    string? ApplicationUserModelId = null);
