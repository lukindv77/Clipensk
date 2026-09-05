namespace Clipensk.Core.Clipboard;

public interface IClipboardTextSearchTextExtractor
{
    ValueTask<string?> TryExtractAsync(
        string formatName,
        string value,
        CancellationToken cancellationToken = default);
}
