using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ClipboardCaptureSourceStageTests
{
    [Fact]
    public async Task ResolveNextAsync_ResolvesLatestPendingRequest()
    {
        var queue = new ClipboardCaptureQueue();
        ClipboardCaptureRequest first = CreateRequest(29);
        ClipboardCaptureRequest latest = CreateRequest(30);
        var sourceApplication = new ClipboardSourceApplication(4242, @"C:\Apps\Source.exe");
        var resolver = new StubSourceApplicationResolver(sourceApplication);
        var stage = new ClipboardCaptureSourceStage(queue, resolver);

        Assert.True(queue.TryEnqueue(first));
        Assert.True(queue.TryEnqueue(latest));

        ClipboardCaptureContext context = await stage.ResolveNextAsync();

        Assert.Equal(latest, context.Request);
        Assert.Equal(sourceApplication, context.SourceApplication);
        Assert.Equal(1, resolver.CallCount);
    }

    [Fact]
    public async Task ResolveNextAsync_AllowsUnknownSourceApplication()
    {
        var queue = new ClipboardCaptureQueue();
        ClipboardCaptureRequest request = CreateRequest(30);
        var resolver = new StubSourceApplicationResolver(null);
        var stage = new ClipboardCaptureSourceStage(queue, resolver);
        Assert.True(queue.TryEnqueue(request));

        ClipboardCaptureContext context = await stage.ResolveNextAsync();

        Assert.Equal(request, context.Request);
        Assert.Null(context.SourceApplication);
        Assert.Equal(1, resolver.CallCount);
    }

    private static ClipboardCaptureRequest CreateRequest(int second)
    {
        return new ClipboardCaptureRequest(
            new EventTimeContext(
                new DateTimeOffset(2026, 9, 5, 10, 15, second, TimeSpan.FromHours(3)),
                "Test/Zone"));
    }

    private sealed class StubSourceApplicationResolver : IClipboardSourceApplicationResolver
    {
        private readonly ClipboardSourceApplication? _sourceApplication;

        public StubSourceApplicationResolver(ClipboardSourceApplication? sourceApplication)
        {
            _sourceApplication = sourceApplication;
        }

        public int CallCount { get; private set; }

        public ClipboardSourceApplication? TryResolveCurrent()
        {
            CallCount++;
            return _sourceApplication;
        }
    }
}
