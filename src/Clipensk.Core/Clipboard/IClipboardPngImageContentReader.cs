namespace Clipensk.Core.Clipboard;

public interface IClipboardPngImageContentReader
{
    bool SupportsFormat(string formatName);

    ValueTask<byte[]> ReadNormalizedPngAsync(
        IClipboardContentSnapshot contentSnapshot,
        string formatName,
        CancellationToken cancellationToken = default);
}
