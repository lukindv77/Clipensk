namespace Clipensk.Core.Clipboard;

public interface IClipboardLinkContentReader
{
    bool SupportsFormat(string formatName);

    ValueTask<Uri> ReadAsync(
        IClipboardContentSnapshot contentSnapshot,
        string formatName);
}
