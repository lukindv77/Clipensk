namespace Clipensk.Core.Clipboard;

public interface IClipboardCustomBinaryContentReader
{
    bool SupportsFormat(string formatName);

    ValueTask<byte[]> ReadAsync(
        IClipboardContentSnapshot contentSnapshot,
        string formatName,
        CancellationToken cancellationToken = default);
}
