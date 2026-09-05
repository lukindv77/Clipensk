using Clipensk.Core.Applications;
using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Xunit;
using DurableApplicationId = Clipensk.Core.Applications.ApplicationId;

namespace Clipensk.Core.Tests;

public sealed class ClipboardCapturePipelineApplicationIdentityTests
{
    [Fact]
    public async Task ProcessNextAsync_ResolvesDurableIdentityBeforePolicyLookup()
    {
        var calls = new List<string>();
        var queue = new ClipboardCaptureQueue();
        Assert.True(queue.TryEnqueue(new ClipboardCaptureRequest(
            new EventTimeContext(
                new DateTimeOffset(2026, 9, 6, 1, 15, 0, TimeSpan.FromHours(7)),
                "Test/Zone"))));

        var source = new ClipboardSourceApplication(4242, @"C:\Apps\Source.exe");
        DurableApplicationId applicationId = DurableApplicationId.New();
        var registry = new OrderedRegistry(applicationId, calls);
        var provider = new CapturingPolicyProvider(calls);
        var pipeline = new ClipboardCapturePipeline(
            new ClipboardCaptureSourceStage(queue, new OrderedSourceResolver(source, calls)),
            new ClipboardCaptureApplicationIdentityStage(registry),
            new ClipboardCapturePolicyResolutionStage(
                provider,
                new ClipboardCapturePolicyEvaluator()),
            new ClipboardFormatDiscoveryStage(new ThrowingSnapshotReader()),
            new ClipboardFormatSelectionStage());

        ClipboardFormatSelection result = await pipeline.ProcessNextAsync();

        Assert.Empty(result.Formats);
        Assert.Equal(new[] { "source", "identity", "policy" }, calls);
        Assert.NotNull(provider.LastContext);
        Assert.Equal(applicationId, provider.LastContext.Value.SourceApplicationId);
        Assert.Equal(source, provider.LastContext.Value.SourceApplication);
    }

    private sealed class OrderedSourceResolver : IClipboardSourceApplicationResolver
    {
        private readonly ClipboardSourceApplication _source;
        private readonly IList<string> _calls;

        public OrderedSourceResolver(ClipboardSourceApplication source, IList<string> calls)
        {
            _source = source;
            _calls = calls;
        }

        public ClipboardSourceApplication? TryResolveCurrent()
        {
            _calls.Add("source");
            return _source;
        }
    }

    private sealed class OrderedRegistry : IApplicationIdentityRegistry
    {
        private readonly DurableApplicationId _applicationId;
        private readonly IList<string> _calls;

        public OrderedRegistry(DurableApplicationId applicationId, IList<string> calls)
        {
            _applicationId = applicationId;
            _calls = calls;
        }

        public ValueTask<ApplicationIdentityResolution?> ResolveOrCreateAsync(
            ApplicationIdentityObservation observation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _calls.Add("identity");
            return ValueTask.FromResult<ApplicationIdentityResolution?>(
                new ApplicationIdentityResolution(
                    _applicationId,
                    ApplicationIdentityResolutionBasis.ExecutablePathAlias,
                    wasCreated: false));
        }
    }

    private sealed class CapturingPolicyProvider : IClipboardCapturePolicyProvider
    {
        private readonly IList<string> _calls;

        public CapturingPolicyProvider(IList<string> calls)
        {
            _calls = calls;
        }

        public ClipboardCaptureContext? LastContext { get; private set; }

        public ValueTask<ClipboardCapturePolicySet> GetPoliciesAsync(
            ClipboardCaptureContext captureContext,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _calls.Add("policy");
            LastContext = captureContext;
            return ValueTask.FromResult(new ClipboardCapturePolicySet(
                new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Deny),
                applicationPolicy: null));
        }
    }

    private sealed class ThrowingSnapshotReader : IClipboardFormatSnapshotReader
    {
        public IClipboardContentSnapshot ReadSnapshot()
        {
            throw new InvalidOperationException("Denied capture must not read clipboard formats.");
        }
    }
}
