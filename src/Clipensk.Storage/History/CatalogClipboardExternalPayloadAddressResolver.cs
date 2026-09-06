using System.Security.Cryptography;
using Clipensk.Storage.ExternalFiles;

namespace Clipensk.Storage.History;

public sealed class CatalogClipboardExternalPayloadAddressResolver :
    IClipboardExternalPayloadAddressResolver
{
    private readonly IExternalPayloadAddressIndex _addressIndex;
    private readonly ExternalPayloadStore _payloadStore;
    private readonly IClipboardCustomBinaryFileExtensionProvider _customBinaryExtensionProvider;

    public CatalogClipboardExternalPayloadAddressResolver(
        IExternalPayloadAddressIndex addressIndex,
        ExternalPayloadStore payloadStore,
        IClipboardCustomBinaryFileExtensionProvider customBinaryExtensionProvider)
    {
        _addressIndex = addressIndex ?? throw new ArgumentNullException(nameof(addressIndex));
        _payloadStore = payloadStore ?? throw new ArgumentNullException(nameof(payloadStore));
        _customBinaryExtensionProvider = customBinaryExtensionProvider ??
            throw new ArgumentNullException(nameof(customBinaryExtensionProvider));
    }

    public async ValueTask<ExternalPayloadAddress> ResolveNormalizedPngAsync(
        DateOnly eventCalendarDate,
        ReadOnlyMemory<byte> pngBytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ExternalPayloadAddress candidate = ExternalPayloadAddressFactory.ForNormalizedPng(
            eventCalendarDate,
            pngBytes.Span);
        ExternalPayloadAddress stored = await _addressIndex.GetOrAddAsync(
            candidate,
            cancellationToken).ConfigureAwait(false);

        await _payloadStore.EnsureStoredAtAddressAsync(
            stored,
            pngBytes,
            cancellationToken).ConfigureAwait(false);
        return stored;
    }

    public async ValueTask<ExternalPayloadAddress> ResolveCustomBinaryAsync(
        DateOnly eventCalendarDate,
        string formatName,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formatName);
        cancellationToken.ThrowIfCancellationRequested();

        string sha256 = Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant();
        ExternalPayloadAddress? existing = await _addressIndex.FindAsync(
            sha256,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.SizeBytes != bytes.Length)
            {
                throw new InvalidDataException(
                    "Existing custom binary address size conflicts with the supplied payload.");
            }

            await _payloadStore.EnsureStoredAtAddressAsync(
                existing,
                bytes,
                cancellationToken).ConfigureAwait(false);
            return existing;
        }

        string extension = await _customBinaryExtensionProvider.GetExtensionAsync(
            formatName,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(extension))
        {
            throw new InvalidDataException(
                "A new custom binary payload requires an explicit non-empty file extension.");
        }

        ExternalPayloadAddress candidate = ExternalPayloadAddressFactory.ForCustomBinary(
            eventCalendarDate,
            bytes.Span,
            extension);
        ExternalPayloadAddress stored = await _addressIndex.GetOrAddAsync(
            candidate,
            cancellationToken).ConfigureAwait(false);

        await _payloadStore.EnsureStoredAtAddressAsync(
            stored,
            bytes,
            cancellationToken).ConfigureAwait(false);
        return stored;
    }
}
