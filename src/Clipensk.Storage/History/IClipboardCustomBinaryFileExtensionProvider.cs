namespace Clipensk.Storage.History;

public interface IClipboardCustomBinaryFileExtensionProvider
{
    ValueTask<string> GetExtensionAsync(
        string formatName,
        CancellationToken cancellationToken = default);
}
