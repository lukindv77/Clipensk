using Clipensk.Core.Clipboard;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace Clipensk.Windows.Clipboard;

internal sealed class WindowsClipboardPngImageContentReader : IClipboardPngImageContentReader
{
    private readonly PngImageNormalizer _normalizer;

    public WindowsClipboardPngImageContentReader(PngImageNormalizer? normalizer = null)
    {
        _normalizer = normalizer ?? new PngImageNormalizer();
    }

    public bool SupportsFormat(string formatName)
    {
        return string.Equals(formatName, StandardDataFormats.Bitmap, StringComparison.Ordinal);
    }

    public async ValueTask<byte[]> ReadNormalizedPngAsync(
        IClipboardContentSnapshot contentSnapshot,
        string formatName)
    {
        ArgumentNullException.ThrowIfNull(contentSnapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(formatName);

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

        RandomAccessStreamReference bitmapReference = await windowsSnapshot.Content.GetBitmapAsync();
        using IRandomAccessStreamWithContentType bitmapStream = await bitmapReference.OpenReadAsync();
        return await _normalizer.NormalizeAsync(bitmapStream);
    }
}
