using Clipensk.Core.Clipboard;
using Windows.ApplicationModel.DataTransfer;

namespace Clipensk.Windows.Clipboard;

internal sealed class WindowsClipboardLinkContentReader : IClipboardLinkContentReader
{
    public bool SupportsFormat(string formatName)
    {
        return string.Equals(formatName, StandardDataFormats.WebLink, StringComparison.Ordinal)
            || string.Equals(formatName, StandardDataFormats.ApplicationLink, StringComparison.Ordinal);
    }

    public async ValueTask<Uri> ReadAsync(
        IClipboardContentSnapshot contentSnapshot,
        string formatName)
    {
        ArgumentNullException.ThrowIfNull(contentSnapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(formatName);

        if (!SupportsFormat(formatName))
        {
            throw new NotSupportedException(
                $"Clipboard format '{formatName}' is not a supported link format.");
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

        if (string.Equals(formatName, StandardDataFormats.WebLink, StringComparison.Ordinal))
        {
            return await windowsSnapshot.Content.GetWebLinkAsync();
        }

        return await windowsSnapshot.Content.GetApplicationLinkAsync();
    }
}
