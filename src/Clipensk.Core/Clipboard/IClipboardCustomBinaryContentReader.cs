namespace Clipensk.Core.Clipboard;

public interface IClipboardCustomBinaryContentReader
{
    bool SupportsFormat(string formatName);

    ValueTask<byte[]?> ReadWithinLimitAsync(
        IClipboardContentSnapshot contentSnapshot,
        string formatName,
        long? maxBytes,
        CancellationToken cancellationToken = default);
}
