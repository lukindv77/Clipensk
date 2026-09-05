using Clipensk.Core.Storage;

namespace Clipensk.Core.Clipboard;

public sealed class ProtectedClipboardAcceptedCaptureDelivery : IClipboardAcceptedCaptureDelivery
{
    private readonly IClipboardAcceptedCaptureDelivery _inner;
    private readonly ProtectedStorageSessionLease _session;

    public ProtectedClipboardAcceptedCaptureDelivery(
        IClipboardAcceptedCaptureDelivery inner,
        ProtectedStorageSessionLease session)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public async ValueTask<bool> ProcessNextAsync(
        CancellationToken cancellationToken = default)
    {
        CancellationToken sessionToken = _session.CancellationToken;
        sessionToken.ThrowIfCancellationRequested();
        cancellationToken.ThrowIfCancellationRequested();

        if (!cancellationToken.CanBeCanceled)
        {
            return await _inner.ProcessNextAsync(sessionToken).ConfigureAwait(false);
        }

        using CancellationTokenSource linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                sessionToken,
                cancellationToken);

        return await _inner
            .ProcessNextAsync(linkedCancellation.Token)
            .ConfigureAwait(false);
    }
}
