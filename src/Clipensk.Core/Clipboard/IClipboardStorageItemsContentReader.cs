namespace Clipensk.Core.Clipboard;

public interface IClipboardStorageItemsContentReader
{
    bool SupportsFormat(string formatName);

    ValueTask<IReadOnlyList<ClipboardStorageItemMetadata>> ReadAsync(
        IClipboardContentSnapshot contentSnapshot,
        string formatName);
}
