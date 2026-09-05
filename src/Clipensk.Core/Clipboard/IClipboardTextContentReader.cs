namespace Clipensk.Core.Clipboard;

public interface IClipboardTextContentReader
{
    bool SupportsFormat(string formatName);

    ValueTask<string> ReadAsync(
        IClipboardContentSnapshot contentSnapshot,
        string formatName);
}
