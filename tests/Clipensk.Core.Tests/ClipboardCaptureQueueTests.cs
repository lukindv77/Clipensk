using Clipensk.Core.Clipboard;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ClipboardCaptureQueueTests
{
    [Fact]
    public async Task EnqueueThenDequeue_ReturnsRequest()
    {
        var queue = new ClipboardCaptureQueue();
        var request = new ClipboardCaptureRequest(DateTimeOffset.UtcNow);

        Assert.True(queue.TryEnqueue(request));

        ClipboardCaptureRequest actual = await queue.DequeueAsync();
        Assert.Equal(request, actual);
    }

    [Fact]
    public async Task MultiplePendingUpdates_CoalesceToLatestRequest()
    {
        var queue = new ClipboardCaptureQueue();
        var first = new ClipboardCaptureRequest(DateTimeOffset.UtcNow.AddSeconds(-1));
        var latest = new ClipboardCaptureRequest(DateTimeOffset.UtcNow);

        Assert.True(queue.TryEnqueue(first));
        Assert.True(queue.TryEnqueue(latest));

        ClipboardCaptureRequest actual = await queue.DequeueAsync();
        Assert.Equal(latest, actual);
    }
}
