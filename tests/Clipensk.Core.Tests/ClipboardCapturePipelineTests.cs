using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ClipboardCapturePipelineTests
{
    [Fact]
    public async Task ProcessNextAsync_ProcessesAllowedCaptureInStageOrder()
    {
        var calls = new List<string>();
        var queue = new ClipboardCaptureQueue();
        ClipboardCaptureRequest request = CreateRequest();
        Assert.True(queue.TryEnqueue(request));

        var sourceApplication = new ClipboardSourceApplication(4242, @"C:\Apps\Source.exe");
        var sourceResolver = new StubSourceResolver(sourceApplication, calls);
        var sourceStage = new ClipboardCaptureSourceStage(queue, sourceResolver);
        var provider = new StubPolicyProvider(
            new ClipboardCapturePolicySet(
                new ClipboardCapturePolicy(
                    ClipboardCapturePolicyRule.Allow,
                    new Dictionary<string, ClipboardFormatCapturePolicy>
                    {
                        ["Text"] = new(ClipboardCapturePolicyRule.Allow),
                    })),
            calls);
        var policyStage = new ClipboardCapturePolicyResolutionStage(
            provider,
            new ClipboardCapturePolicyEvaluator());
        var snapshotReader = new StubSnapshotReader(new[] { "Text", "Unknown" }, calls);
        var discoveryStage = new ClipboardFormatDiscoveryStage(snapshotReader);
        var pipeline = new ClipboardCapturePipeline(
            sourceStage,
            policyStage,
            discoveryStage,
            new ClipboardFormatSelectionStage());

        ClipboardFormatSelection result = await pipeline.ProcessNextAsync();

        Assert.Single(result.Formats);
        Assert.Equal("Text", result.Formats[0].FormatName);
        Assert.Equal(request, result.Snapshot.PolicyContext.CaptureContext.Request);
        Assert.Equal(sourceApplication, result.Snapshot.PolicyContext.CaptureContext.SourceApplication);
        Assert.Equal(new[] { "source", "policy", "formats" }, calls);
    }

    [Fact]
    public async Task ProcessNextAsync_DeniedCaptureDoesNotReadClipboardFormats()
    {
        var calls = new List<string>();
        var queue = new ClipboardCaptureQueue();
        Assert.True(queue.TryEnqueue(CreateRequest()));

        var sourceStage = new ClipboardCaptureSourceStage(
            queue,
            new StubSourceResolver(sourceApplication: null, calls));
        var policyStage = new ClipboardCapturePolicyResolutionStage(
            new StubPolicyProvider(
                new ClipboardCapturePolicySet(
                    new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Deny)),
                calls),
            new ClipboardCapturePolicyEvaluator());
        var snapshotReader = new StubSnapshotReader(new[] { "Text" }, calls);
        var pipeline = new ClipboardCapturePipeline(
            sourceStage,
            policyStage,
            new ClipboardFormatDiscoveryStage(snapshotReader),
            new ClipboardFormatSelectionStage());

        ClipboardFormatSelection result = await pipeline.ProcessNextAsync();

        Assert.Empty(result.Formats);
        Assert.Equal(0, snapshotReader.CallCount);
        Assert.Equal(new[] { "source", "policy" }, calls);
    }

    private static ClipboardCaptureRequest CreateRequest()
    {
        return new ClipboardCaptureRequest(
            new EventTimeContext(
                new DateTimeOffset(2026, 9, 5, 10, 15, 30, TimeSpan.FromHours(3)),
                "Test/Zone"));
    }

    private sealed class StubSourceResolver : IClipboardSourceApplicationResolver
    {
        private readonly ClipboardSourceApplication? _sourceApplication;
        private readonly IList<string> _calls;

        public StubSourceResolver(
            ClipboardSourceApplication? sourceApplication,
            IList<string> calls)
        {
            _sourceApplication = sourceApplication;
            _calls = calls;
        }

        public ClipboardSourceApplication? TryResolveCurrent()
        {
            _calls.Add("source");
            return _sourceApplication;
        }
    }

    private sealed class StubPolicyProvider : IClipboardCapturePolicyProvider
    {
        private readonly ClipboardCapturePolicySet _policies;
        private readonly IList<string> _calls;

        public StubPolicyProvider(
            ClipboardCapturePolicySet policies,
            IList<string> calls)
        {
            _policies = policies;
            _calls = calls;
        }

        public ValueTask<ClipboardCapturePolicySet> GetPoliciesAsync(
            ClipboardCaptureContext captureContext,
            CancellationToken cancellationToken = default)
        {
            _calls.Add("policy");
            return ValueTask.FromResult(_policies);
        }
    }

    private sealed class StubSnapshotReader : IClipboardFormatSnapshotReader
    {
        private readonly IReadOnlyList<string> _formats;
        private readonly IList<string> _calls;

        public StubSnapshotReader(
            IReadOnlyList<string> formats,
            IList<string> calls)
        {
            _formats = formats;
            _calls = calls;
        }

        public int CallCount { get; private set; }

        public IClipboardContentSnapshot ReadSnapshot()
        {
            CallCount++;
            _calls.Add("formats");
            return new StubContentSnapshot(_formats);
        }
    }

    private sealed class StubContentSnapshot : IClipboardContentSnapshot
    {
        public StubContentSnapshot(IReadOnlyList<string> formats)
        {
            AvailableFormats = formats;
        }

        public IReadOnlyList<string> AvailableFormats { get; }
    }
}
