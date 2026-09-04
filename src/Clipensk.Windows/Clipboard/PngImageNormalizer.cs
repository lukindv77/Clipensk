using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Clipensk.Windows.Clipboard;

public sealed class PngImageNormalizer
{
    public async Task<byte[]> NormalizeAsync(IRandomAccessStream source)
    {
        ArgumentNullException.ThrowIfNull(source);

        source.Seek(0);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(source);

        using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied);

        using var output = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();

        if (output.Size > int.MaxValue)
        {
            throw new InvalidOperationException("Нормализованное изображение превышает поддерживаемый размер Clipensk.");
        }

        output.Seek(0);
        byte[] result = new byte[(int)output.Size];
        using var reader = new DataReader(output.GetInputStreamAt(0));
        await reader.LoadAsync((uint)result.Length);
        reader.ReadBytes(result);
        return result;
    }
}
