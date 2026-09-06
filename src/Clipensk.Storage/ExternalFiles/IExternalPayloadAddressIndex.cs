namespace Clipensk.Storage.ExternalFiles;

public interface IExternalPayloadAddressIndex
{
    ValueTask<ExternalPayloadAddress?> FindAsync(
        string sha256,
        CancellationToken cancellationToken = default);

    ValueTask<ExternalPayloadAddress> GetOrAddAsync(
        ExternalPayloadAddress candidate,
        CancellationToken cancellationToken = default);
}
