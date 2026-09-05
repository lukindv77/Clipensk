using System.Threading.Channels;

namespace Clipensk.Core.Clipboard;

public sealed class ClipboardCaptureQueue
{
    private readonly Channel<ClipboardCaptureRequest> _channel = Channel.CreateBounded<ClipboardCaptureRequest>(
        new BoundedChannelOptions(1)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    public bool TryEnqueue(ClipboardCaptureRequest request)
    {
        return _channel.Writer.TryWrite(request);
    }

    public ValueTask<ClipboardCaptureRequest> DequeueAsync(CancellationToken cancellationToken = default)
    {
        return _channel.Reader.ReadAsync(cancellationToken);
    }
}
