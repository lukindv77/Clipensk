using Clipensk.Storage.ExternalFiles;

namespace Clipensk.Storage.History;

public interface IClipboardExternalPayloadAddressResolver
{
    ValueTask<ExternalPayloadAddress> ResolveNormalizedPngAsync(
        DateOnly eventCalendarDate,
        ReadOnlyMemory<byte> pngBytes,
        CancellationToken cancellationToken = default);

    ValueTask<ExternalPayloadAddress> ResolveCustomBinaryAsync(
        DateOnly eventCalendarDate,
        string formatName,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default);
}
