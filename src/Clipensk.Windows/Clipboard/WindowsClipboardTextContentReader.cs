using Clipensk.Core.Clipboard;
using Windows.ApplicationModel.DataTransfer;

namespace Clipensk.Windows.Clipboard;

internal sealed class WindowsClipboardTextContentReader : IClipboardTextContentReader
{
    public bool SupportsFormat(string formatName)
    {
        return string.Equals(formatName, StandardDataFormats.Text, StringComparison.Ordinal)
            || string.Equals(formatName, StandardDataFormats.Html, StringComparison.Ordinal)
            || string.Equals(formatName, StandardDataFormats.Rtf, StringComparison.Ordinal);
    }

    public async ValueTask<string> ReadAsync(
        IClipboardContentSnapshot contentSnapshot,
        string formatName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contentSnapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(formatName);
        cancellationToken.ThrowIfCancellationRequested();

        if (!SupportsFormat(formatName))
        {
            throw new NotSupportedException($"Clipboard format '{formatName}' is not a supported standard text format.");
        }

        if (contentSnapshot is not WindowsClipboardContentSnapshot windowsSnapshot)
        {
            throw new ArgumentException(
                "Clipboard content snapshot was not created by the Windows clipboard reader.",
                nameof(contentSnapshot));
        }

        if (!windowsSnapshot.AvailableFormats.Contains(formatName, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Clipboard content snapshot does not contain format '{formatName}'.");
        }

        DataPackageView content = windowsSnapshot.Content;
        if (string.Equals(formatName, StandardDataFormats.Text, StringComparison.Ordinal))
        {
            return await content.GetTextAsync().AsTask(cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(formatName, StandardDataFormats.Html, StringComparison.Ordinal))
        {
            return await content.GetHtmlFormatAsync().AsTask(cancellationToken).ConfigureAwait(false);
        }

        return await content.GetRtfAsync().AsTask(cancellationToken).ConfigureAwait(false);
    }
}
