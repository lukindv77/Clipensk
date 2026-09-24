using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Clipensk.Windows.Clipboard;

/// <remarks>
/// The source stream may come from the clipboard, bound to the clipboard thread's apartment: the
/// awaits resume on the caller's context so the decoder keeps reading it there
/// (<see cref="ClipboardStaThread"/>).
/// </remarks>
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
            .AsTask(cancellationToken);

        using SoftwareBitmap bitmap = await decoder
            .GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied)
            .AsTask(cancellationToken);

        using var output = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder
            .CreateAsync(BitmapEncoder.PngEncoderId, output)
            .AsTask(cancellationToken);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync().AsTask(cancellationToken);

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
            .AsTask(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        reader.ReadBytes(result);
        return result;
    }
}
