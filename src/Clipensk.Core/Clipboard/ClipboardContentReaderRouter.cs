namespace Clipensk.Core.Clipboard;

public sealed class ClipboardContentReaderRouter
{
    private readonly IClipboardTextContentReader _textReader;
    private readonly IClipboardPngImageContentReader _pngImageReader;
    private readonly IClipboardLinkContentReader _linkReader;
    private readonly IClipboardStorageItemsContentReader _storageItemsReader;

    public ClipboardContentReaderRouter(
        IClipboardTextContentReader textReader,
        IClipboardPngImageContentReader pngImageReader,
        IClipboardLinkContentReader linkReader,
        IClipboardStorageItemsContentReader storageItemsReader)
    {
        _textReader = textReader ?? throw new ArgumentNullException(nameof(textReader));
        _pngImageReader = pngImageReader ?? throw new ArgumentNullException(nameof(pngImageReader));
        _linkReader = linkReader ?? throw new ArgumentNullException(nameof(linkReader));
        _storageItemsReader = storageItemsReader ?? throw new ArgumentNullException(nameof(storageItemsReader));
    }

    public ClipboardContentReaderRoute? TryRoute(ClipboardSelectedFormat selectedFormat)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedFormat.FormatName);

        ClipboardContentReaderKind? readerKind = null;

        Match(ClipboardContentReaderKind.Text, _textReader.SupportsFormat(selectedFormat.FormatName));
        Match(ClipboardContentReaderKind.PngImage, _pngImageReader.SupportsFormat(selectedFormat.FormatName));
        Match(ClipboardContentReaderKind.Link, _linkReader.SupportsFormat(selectedFormat.FormatName));
        Match(ClipboardContentReaderKind.StorageItems, _storageItemsReader.SupportsFormat(selectedFormat.FormatName));

        return readerKind is null
            ? null
            : new ClipboardContentReaderRoute(selectedFormat, readerKind.Value);

        void Match(ClipboardContentReaderKind candidate, bool supported)
        {
            if (!supported)
            {
                return;
            }

            if (readerKind is not null)
            {
                throw new InvalidOperationException(
                    $"Clipboard format '{selectedFormat.FormatName}' is supported by more than one content reader.");
            }

            readerKind = candidate;
        }
    }
}
