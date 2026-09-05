using Clipensk.Core.Clipboard;
using Windows.Storage.Streams;

namespace Clipensk.Windows.Clipboard;

internal sealed class WindowsClipboardCustomBinaryContentReader : IClipboardCustomBinaryContentReader
{
    public bool SupportsFormat(string formatName)
    {
        return !string.IsNullOrWhiteSpace(formatName);
    }

    public async ValueTask<byte[]?> ReadWithinLimitAsync(
        IClipboardContentSnapshot contentSnapshot,
        string formatName,
        long? maxBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contentSnapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(formatName);
        if (maxBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }
        cancellationToken.ThrowIfCancellationRequested();

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

        object value = await windowsSnapshot.Content
            .GetDataAsync(formatName)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (value is not IRandomAccessStream stream)
        {
            throw new NotSupportedException(
                $"Clipboard format '{formatName}' did not expose RandomAccessStream binary data.");
        }

        using (stream)
        {
            if (maxBytes.HasValue && stream.Size > (ulong)maxBytes.Value)
            {
                return null;
            }

            if (stream.Size > int.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Clipboard binary format '{formatName}' exceeds the supported in-memory size.");
            }

            stream.Seek(0);
            byte[] bytes = new byte[(int)stream.Size];
            if (bytes.Length == 0)
            {
                return bytes;
            }

            using var reader = new DataReader(stream.GetInputStreamAt(0));
            uint loaded = await reader
                .LoadAsync((uint)bytes.Length)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (loaded != bytes.Length)
            {
                throw new InvalidDataException(
                    $"Clipboard binary format '{formatName}' ended before its declared stream size.");
            }

            reader.ReadBytes(bytes);
            return bytes;
        }
    }
}
