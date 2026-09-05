using Clipensk.Core.Clipboard;
using Windows.ApplicationModel.DataTransfer;

namespace Clipensk.Windows.Clipboard;

internal sealed class WindowsClipboardTextSearchTextExtractor : IClipboardTextSearchTextExtractor
{
    private readonly IClipboardHtmlSearchTextConverter _htmlConverter;

    public WindowsClipboardTextSearchTextExtractor(IClipboardHtmlSearchTextConverter htmlConverter)
    {
        _htmlConverter = htmlConverter ?? throw new ArgumentNullException(nameof(htmlConverter));
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

        // RTF remains an explicit implementation blocker: do not fake searchable text
        // by indexing raw RTF control syntax.
        return ValueTask.FromResult<string?>(null);
    }
}
