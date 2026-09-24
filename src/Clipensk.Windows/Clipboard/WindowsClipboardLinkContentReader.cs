using Clipensk.Core.Clipboard;
using Windows.ApplicationModel.DataTransfer;

namespace Clipensk.Windows.Clipboard;

internal sealed class WindowsClipboardLinkContentReader : IClipboardLinkContentReader
{
    private readonly ClipboardStaThread _clipboardThread;

    public WindowsClipboardLinkContentReader(ClipboardStaThread clipboardThread)
    {
        _clipboardThread = clipboardThread ?? throw new ArgumentNullException(nameof(clipboardThread));
    }

    public bool SupportsFormat(string formatName)
    {
        return string.Equals(formatName, StandardDataFormats.WebLink, StringComparison.Ordinal)
            || string.Equals(formatName, StandardDataFormats.ApplicationLink, StringComparison.Ordinal);
    }

    public ValueTask<Uri> ReadAsync(
        IClipboardContentSnapshot contentSnapshot,
        string formatName,
        CancellationToken cancellationToken = default) =>
        new(_clipboardThread.RunAsync(
            () => ReadOnClipboardThreadAsync(contentSnapshot, formatName, cancellationToken),
            cancellationToken));

    // Runs on the clipboard thread: every await resumes there, as the clipboard objects require.
    private async Task<Uri> ReadOnClipboardThreadAsync(
        IClipboardContentSnapshot contentSnapshot,
        string formatName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contentSnapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(formatName);
        cancellationToken.ThrowIfCancellationRequested();

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
            return await windowsSnapshot.Content
                .GetWebLinkAsync()
                .AsTask(cancellationToken);
        }

        return await windowsSnapshot.Content
            .GetApplicationLinkAsync()
            .AsTask(cancellationToken);
    }
}
