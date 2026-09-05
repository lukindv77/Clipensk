using Clipensk.Core.Clipboard;
using Windows.ApplicationModel.DataTransfer;
using Windows.Data.Html;

namespace Clipensk.Windows.Clipboard;

internal sealed class WindowsClipboardTextSearchTextExtractor : IClipboardTextSearchTextExtractor
{
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

        if (!string.Equals(formatName, StandardDataFormats.Html, StringComparison.Ordinal))
        {
            return ValueTask.FromResult<string?>(null);
        }

        string fragment = HtmlFormatHelper.GetStaticFragment(value);
        cancellationToken.ThrowIfCancellationRequested();
        string searchText = HtmlUtilities.ConvertToText(fragment);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<string?>(searchText);
    }
}
