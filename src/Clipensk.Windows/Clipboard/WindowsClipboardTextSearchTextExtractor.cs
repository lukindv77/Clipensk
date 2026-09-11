using Clipensk.Core.Clipboard;
using Windows.ApplicationModel.DataTransfer;

namespace Clipensk.Windows.Clipboard;

internal sealed class WindowsClipboardTextSearchTextExtractor : IClipboardTextSearchTextExtractor
{
    private readonly IClipboardHtmlSearchTextConverter _htmlConverter;
    private readonly IClipboardRtfSearchTextConverter _rtfConverter;

    public WindowsClipboardTextSearchTextExtractor(
        IClipboardHtmlSearchTextConverter htmlConverter,
        IClipboardRtfSearchTextConverter rtfConverter)
    {
        _htmlConverter = htmlConverter ?? throw new ArgumentNullException(nameof(htmlConverter));
        _rtfConverter = rtfConverter ?? throw new ArgumentNullException(nameof(rtfConverter));
    }

    public ValueTask<string?> TryExtractAsync(
        string formatName,
        string value,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formatName);
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.Equals(formatName, StandardDataFormats.Text, StringComparison.Ordinal))
        {
            return ValueTask.FromResult<string?>(value);
        }

        if (string.Equals(formatName, StandardDataFormats.Html, StringComparison.Ordinal))
        {
            string? searchText = _htmlConverter.TryConvert(value);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(searchText);
        }

        if (string.Equals(formatName, StandardDataFormats.Rtf, StringComparison.Ordinal))
        {
            string? searchText = _rtfConverter.TryConvert(value);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(searchText);
        }

        return ValueTask.FromResult<string?>(null);
    }
}
