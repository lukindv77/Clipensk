using System.Threading.Channels;

namespace Clipensk.Core.Clipboard;

public sealed class ClipboardCaptureQueue
{
    private readonly Channel<QueuedCaptureRequest> _channel = Channel.CreateBounded<QueuedCaptureRequest>(
        new BoundedChannelOptions(1)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    private long _captureEpoch;

    public long BeginCaptureEpoch()
    {
        return Interlocked.Increment(ref _captureEpoch);
    }

    public void InvalidateCaptureEpoch(long captureEpoch)
    {
        Interlocked.CompareExchange(
            ref _captureEpoch,
            unchecked(captureEpoch + 1),
            captureEpoch);
    }

    public bool TryEnqueue(ClipboardCaptureRequest request)
    {
        return TryEnqueue(request, Volatile.Read(ref _captureEpoch));
    }

    public bool TryEnqueue(ClipboardCaptureRequest request, long captureEpoch)
    {
        if (captureEpoch != Volatile.Read(ref _captureEpoch))
        {
            return false;
        }

        return _channel.Writer.TryWrite(new QueuedCaptureRequest(captureEpoch, request));
    }

    public async ValueTask<ClipboardCaptureRequest> DequeueAsync(
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            QueuedCaptureRequest queued = await _channel.Reader
                .ReadAsync(cancellationToken)
                .ConfigureAwait(false);

            if (queued.CaptureEpoch == Volatile.Read(ref _captureEpoch))
            {
                return queued.Request;
            }
        }
    }

    private readonly record struct QueuedCaptureRequest(
        long CaptureEpoch,
        ClipboardCaptureRequest Request);
}
