using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ClipboardCaptureReadPlanningPipelineTests
{
    [Fact]
    public async Task ProcessNextAsync_ProducesRoutesAndExplicitUnsupportedFormats()
    {
        var queue = new ClipboardCaptureQueue();
        Assert.True(queue.TryEnqueue(
            new ClipboardCaptureRequest(
                new EventTimeContext(
                    new DateTimeOffset(2026, 9, 5, 10, 35, 0, TimeSpan.FromHours(3)),
                    "Test/Zone"))));

        var capturePipeline = new ClipboardCapturePipeline(
            new ClipboardCaptureSourceStage(queue, new StubSourceResolver()),
            new ClipboardCapturePolicyResolutionStage(
                new StubPolicyProvider(
                    new ClipboardCapturePolicySet(
                        new ClipboardCapturePolicy(
                            ClipboardCapturePolicyRule.Allow,
                            new Dictionary<string, ClipboardFormatCapturePolicy>
                            {
                                ["Text"] = new(ClipboardCapturePolicyRule.Allow, 1024),
                                ["Custom.Binary"] = new(ClipboardCapturePolicyRule.Allow, 4096),
                            }))),
                new ClipboardCapturePolicyEvaluator()),
            new ClipboardFormatDiscoveryStage(
                new StubSnapshotReader(new[] { "Text", "Custom.Binary" })),
            new ClipboardFormatSelectionStage());
        var readPlanStage = new ClipboardContentReadPlanStage(
            new ClipboardContentReaderRouter(
                new StubTextReader("Text"),
                new StubPngImageReader(),
                new StubLinkReader(),
                new StubStorageItemsReader()));
        var pipeline = new ClipboardCaptureReadPlanningPipeline(capturePipeline, readPlanStage);

        ClipboardContentReadPlan result = await pipeline.ProcessNextAsync();

        Assert.Single(result.Routes);
        Assert.Equal(ClipboardContentReaderKind.Text, result.Routes[0].ReaderKind);
        Assert.Equal("Text", result.Routes[0].SelectedFormat.FormatName);
        Assert.Equal(1024, result.Routes[0].SelectedFormat.MaxBytes);
        Assert.Single(result.UnsupportedFormats);
        Assert.Equal("Custom.Binary", result.UnsupportedFormats[0].FormatName);
        Assert.Equal(4096, result.UnsupportedFormats[0].MaxBytes);
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

    private abstract class StubReaderBase
    {
        private readonly string? _supportedFormat;

        protected StubReaderBase(string? supportedFormat)
        {
            _supportedFormat = supportedFormat;
        }

        protected bool Supports(string formatName)
        {
            return string.Equals(formatName, _supportedFormat, StringComparison.Ordinal);
        }
    }

    private sealed class StubTextReader : StubReaderBase, IClipboardTextContentReader
    {
        public StubTextReader(string? supportedFormat = null)
            : base(supportedFormat)
        {
        }

        public bool SupportsFormat(string formatName) => Supports(formatName);

        public ValueTask<string> ReadAsync(
            IClipboardContentSnapshot contentSnapshot,
            string formatName) => throw new InvalidOperationException("Read must not be called while planning.");
    }

    private sealed class StubPngImageReader : StubReaderBase, IClipboardPngImageContentReader
    {
        public StubPngImageReader(string? supportedFormat = null)
            : base(supportedFormat)
        {
        }

        public bool SupportsFormat(string formatName) => Supports(formatName);

        public ValueTask<byte[]> ReadNormalizedPngAsync(
            IClipboardContentSnapshot contentSnapshot,
            string formatName) => throw new InvalidOperationException("Read must not be called while planning.");
    }

    private sealed class StubLinkReader : StubReaderBase, IClipboardLinkContentReader
    {
        public StubLinkReader(string? supportedFormat = null)
            : base(supportedFormat)
        {
        }

        public bool SupportsFormat(string formatName) => Supports(formatName);

        public ValueTask<Uri> ReadAsync(
            IClipboardContentSnapshot contentSnapshot,
            string formatName) => throw new InvalidOperationException("Read must not be called while planning.");
    }

    private sealed class StubStorageItemsReader : StubReaderBase, IClipboardStorageItemsContentReader
    {
        public StubStorageItemsReader(string? supportedFormat = null)
            : base(supportedFormat)
        {
        }

        public bool SupportsFormat(string formatName) => Supports(formatName);

        public ValueTask<IReadOnlyList<ClipboardStorageItemMetadata>> ReadAsync(
            IClipboardContentSnapshot contentSnapshot,
            string formatName) => throw new InvalidOperationException("Read must not be called while planning.");
    }
}
