using Clipensk.Core.Clipboard;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace Clipensk.Windows.Clipboard;

internal sealed class WindowsClipboardPngImageContentReader : IClipboardPngImageContentReader
{
    private readonly ClipboardStaThread _clipboardThread;
    private readonly PngImageNormalizer _normalizer;

    public WindowsClipboardPngImageContentReader(
        ClipboardStaThread clipboardThread,
        PngImageNormalizer? normalizer = null)
    {
        _clipboardThread = clipboardThread ?? throw new ArgumentNullException(nameof(clipboardThread));
        _normalizer = normalizer ?? new PngImageNormalizer();
    }

    public bool SupportsFormat(string formatName)
    {
        return string.Equals(formatName, StandardDataFormats.Bitmap, StringComparison.Ordinal);
    }

    public ValueTask<byte[]> ReadNormalizedPngAsync(
        IClipboardContentSnapshot contentSnapshot,
        string formatName,
        CancellationToken cancellationToken = default) =>
        new(_clipboardThread.RunAsync(
            () => ReadNormalizedPngOnClipboardThreadAsync(contentSnapshot, formatName, cancellationToken),
            cancellationToken));

    // Runs on the clipboard thread: every await resumes there, as the clipboard objects require.
    private async Task<byte[]> ReadNormalizedPngOnClipboardThreadAsync(
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
                $"Clipboard format '{formatName}' is not the supported bitmap format.");
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

        RandomAccessStreamReference bitmapReference = await windowsSnapshot.Content
            .GetBitmapAsync()
            .AsTask(cancellationToken);
        using IRandomAccessStreamWithContentType bitmapStream = await bitmapReference
            .OpenReadAsync()
            .AsTask(cancellationToken);
        return await _normalizer
            .NormalizeAsync(bitmapStream, cancellationToken);
    }
}
