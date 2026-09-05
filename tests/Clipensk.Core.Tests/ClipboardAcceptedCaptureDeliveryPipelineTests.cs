using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ClipboardAcceptedCaptureDeliveryPipelineTests
{
    [Fact]
    public async Task ProcessNextAsync_DeliversAcceptedPayloadAndPassesSameCancellationToken()
    {
        var textReader = new StubTextReader("Text", "accepted");
        var sink = new RecordingSink();
        ClipboardAcceptedCaptureDeliveryPipeline pipeline = CreatePipeline(
            textReader,
            sink,
            maxBytes: null);
        using var cancellation = new CancellationTokenSource();

        bool delivered = await pipeline.ProcessNextAsync(cancellation.Token);

        Assert.True(delivered);
        Assert.Equal(cancellation.Token, textReader.ObservedCancellationToken);
        Assert.Equal(cancellation.Token, sink.ObservedCancellationToken);
        ClipboardAcceptedCapture stored = Assert.Single(sink.Stored);
        ClipboardCapturedTextContent content = Assert.IsType<ClipboardCapturedTextContent>(
            Assert.Single(stored.Content));
        Assert.Equal("accepted", content.Value);
    }

    [Fact]
    public async Task ProcessNextAsync_DoesNotCallSinkWhenPayloadIsSizeRejected()
    {
        var textReader = new StubTextReader("Text", "ЖЖ");
        var sink = new RecordingSink();
        ClipboardAcceptedCaptureDeliveryPipeline pipeline = CreatePipeline(
            textReader,
            sink,
            maxBytes: 3);

        bool delivered = await pipeline.ProcessNextAsync();

        Assert.False(delivered);
        Assert.Empty(sink.Stored);
        Assert.Equal(1, textReader.ReadCount);
    }

    private static ClipboardAcceptedCaptureDeliveryPipeline CreatePipeline(
        StubTextReader textReader,
        RecordingSink sink,
        long? maxBytes)
    {
        var queue = new ClipboardCaptureQueue();
        Assert.True(queue.TryEnqueue(
            new ClipboardCaptureRequest(
                new EventTimeContext(
                    new DateTimeOffset(2026, 9, 5, 10, 35, 0, TimeSpan.FromHours(3)),
                    "Test/Zone"))));

        var policy = new ClipboardCapturePolicy(
            ClipboardCapturePolicyRule.Allow,
            new Dictionary<string, ClipboardFormatCapturePolicy>
            {
                ["Text"] = new(ClipboardCapturePolicyRule.Allow, maxBytes),
            });
        var capturePipeline = new ClipboardCapturePipeline(
            new ClipboardCaptureSourceStage(queue, new StubSourceResolver()),
            new ClipboardCapturePolicyResolutionStage(
                new StubPolicyProvider(new ClipboardCapturePolicySet(policy)),
                new ClipboardCapturePolicyEvaluator()),
            new ClipboardFormatDiscoveryStage(
                new StubSnapshotReader(["Text"])),
            new ClipboardFormatSelectionStage());

        var pngReader = new StubPngImageReader();
        var linkReader = new StubLinkReader();
        var storageItemsReader = new StubStorageItemsReader();
        var router = new ClipboardContentReaderRouter(
            textReader,
            pngReader,
            linkReader,
            storageItemsReader);
        var executionPipeline = new ClipboardCaptureReadExecutionPipeline(
            new ClipboardCaptureReadPlanningPipeline(
                capturePipeline,
                new ClipboardContentReadPlanStage(router)),
            new ClipboardContentReadExecutionStage(
                textReader,
                pngReader,
                linkReader,
                storageItemsReader));

        return new ClipboardAcceptedCaptureDeliveryPipeline(
            executionPipeline,
            new ClipboardAcceptedCaptureSinkStage(
                new ClipboardAcceptedCaptureStage(),
                sink));
    }

    private sealed class StubSourceResolver : IClipboardSourceApplicationResolver
    {
        public ClipboardSourceApplication? TryResolveCurrent() => null;
    }

    private sealed class StubPolicyProvider : IClipboardCapturePolicyProvider
    {
        private readonly ClipboardCapturePolicySet _policies;

        public StubPolicyProvider(ClipboardCapturePolicySet policies)
        {
            _policies = policies;
        }

        public ValueTask<ClipboardCapturePolicySet> GetPoliciesAsync(
            ClipboardCaptureContext captureContext,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_policies);
        }
    }

    private sealed class StubSnapshotReader : IClipboardFormatSnapshotReader
    {
        private readonly IReadOnlyList<string> _formats;

        public StubSnapshotReader(IReadOnlyList<string> formats)
        {
            _formats = formats;
        }

        public IClipboardContentSnapshot ReadSnapshot() => new StubContentSnapshot(_formats);
    }

    private sealed class StubContentSnapshot : IClipboardContentSnapshot
    {
        public StubContentSnapshot(IReadOnlyList<string> formats)
        {
            AvailableFormats = formats;
        }

        public IReadOnlyList<string> AvailableFormats { get; }
    }

    private sealed class StubTextReader : IClipboardTextContentReader
    {
        private readonly string _format;
        private readonly string _value;

        public StubTextReader(string format, string value)
        {
            _format = format;
            _value = value;
        }

        public int ReadCount { get; private set; }

        public CancellationToken ObservedCancellationToken { get; private set; }

        public bool SupportsFormat(string formatName) =>
            string.Equals(formatName, _format, StringComparison.Ordinal);

        public ValueTask<string> ReadAsync(
            IClipboardContentSnapshot contentSnapshot,
            string formatName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            ObservedCancellationToken = cancellationToken;
            return ValueTask.FromResult(_value);
        }
    }

    private sealed class StubPngImageReader : IClipboardPngImageContentReader
    {
        public bool SupportsFormat(string formatName) => false;

        public ValueTask<byte[]> ReadNormalizedPngAsync(
            IClipboardContentSnapshot contentSnapshot,
            string formatName,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("PNG reader must not be called.");
    }

    private sealed class StubLinkReader : IClipboardLinkContentReader
    {
        public bool SupportsFormat(string formatName) => false;

        public ValueTask<Uri> ReadAsync(
            IClipboardContentSnapshot contentSnapshot,
            string formatName,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Link reader must not be called.");
    }

    private sealed class StubStorageItemsReader : IClipboardStorageItemsContentReader
    {
        public bool SupportsFormat(string formatName) => false;

        public ValueTask<IReadOnlyList<ClipboardStorageItemMetadata>> ReadAsync(
            IClipboardContentSnapshot contentSnapshot,
            string formatName,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("StorageItems reader must not be called.");
    }

    private sealed class RecordingSink : IClipboardAcceptedCaptureSink
    {
        public List<ClipboardAcceptedCapture> Stored { get; } = [];

        public CancellationToken ObservedCancellationToken { get; private set; }

        public ValueTask StoreAsync(
            ClipboardAcceptedCapture capture,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObservedCancellationToken = cancellationToken;
            Stored.Add(capture);
            return ValueTask.CompletedTask;
        }
    }
}
