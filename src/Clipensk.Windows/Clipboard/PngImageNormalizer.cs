using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Clipensk.Windows.Clipboard;

public sealed class PngImageNormalizer
{
    public async Task<byte[]> NormalizeAsync(
        IRandomAccessStream source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();

        source.Seek(0);
        BitmapDecoder decoder = await BitmapDecoder
            .CreateAsync(source)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        using SoftwareBitmap bitmap = await decoder
            .GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        using var output = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder
            .CreateAsync(BitmapEncoder.PngEncoderId, output)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);

        if (output.Size > int.MaxValue)
        {
            throw new InvalidOperationException("Нормализованное изображение превышает поддерживаемый размер Clipensk.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        output.Seek(0);
        byte[] result = new byte[(int)output.Size];
        using var reader = new DataReader(output.GetInputStreamAt(0));
        await reader
            .LoadAsync((uint)result.Length)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        reader.ReadBytes(result);
        return result;
    }
}
