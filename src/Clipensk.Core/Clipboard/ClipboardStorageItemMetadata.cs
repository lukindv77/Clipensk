namespace Clipensk.Core.Clipboard;

public readonly record struct ClipboardStorageItemMetadata(
    string FullPath,
    string Name,
    string Extension,
    bool IsDirectory,
    int Order,
    ClipboardPreferredFileOperation PreferredOperation);
