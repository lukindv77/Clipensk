using Clipensk.Core.Applications;
using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using ApplicationId = Clipensk.Core.Applications.ApplicationId;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ClipboardApplicationDiscoveredFormatObservationTests
{
    [Fact]
    public async Task ProcessNextAsync_ObservesExactDiscoveredFormatsForDurableApplication()
    {
        var calls = new List<string>();
        var queue = new ClipboardCaptureQueue();
        var timestamp = new DateTimeOffset(2026, 9, 11, 1, 15, 30, TimeSpan.FromHours(7));
        Assert.True(queue.TryEnqueue(new ClipboardCaptureRequest(
            new EventTimeContext(timestamp, "Test/Zone"))));

        var source = new ClipboardSourceApplication(4242, @"C:\Apps\Observed.exe");
        ApplicationId applicationId = ApplicationId.New();
        var observer = new RecordingObserver(calls);
        var pipeline = new ClipboardCapturePipeline(
            new ClipboardCaptureSourceStage(queue, new StubSourceResolver(source, calls)),
            new ClipboardCaptureApplicationIdentityStage(
                new StubIdentityRegistry(applicationId, calls)),
            new ClipboardCapturePolicyResolutionStage(
                new StubPolicyProvider(AllowedPolicy(), calls),
                new ClipboardCapturePolicyEvaluator()),
            new ClipboardFormatDiscoveryStage(
                new StubSnapshotReader(["Text", "HTML Format", "Text", "Contoso.Custom"], calls)),
            new ClipboardApplicationDiscoveredFormatObservationStage(observer),
            new ClipboardFormatSelectionStage());

        ClipboardFormatSelection result = await pipeline.ProcessNextAsync();

        Assert.Equal(new[] { "source", "identity", "policy", "formats", "observe" }, calls);
        Assert.Equal(applicationId, observer.ApplicationId);
        Assert.Equal(
            new[] { "Text", "HTML Format", "Text", "Contoso.Custom" },
            observer.FormatNames);
        Assert.Equal(timestamp.ToUniversalTime(), observer.ObservedAtUtc);
        Assert.Equal(2, result.Formats.Count);
        Assert.Equal("Text", result.Formats[0].FormatName);
        Assert.Equal("HTML Format", result.Formats[1].FormatName);
    }

    [Fact]
    public async Task ProcessNextAsync_DeniedCaptureDoesNotReadOrObserveFormats()
    {
        var calls = new List<string>();
        var queue = new ClipboardCaptureQueue();
        Assert.True(queue.TryEnqueue(Request()));
        var observer = new RecordingObserver(calls);
        var snapshotReader = new ThrowingSnapshotReader();

        var pipeline = new ClipboardCapturePipeline(
            new ClipboardCaptureSourceStage(
                queue,
                new StubSourceResolver(
                    new ClipboardSourceApplication(4242, @"C:\Apps\Denied.exe"),
                    calls)),
            new ClipboardCaptureApplicationIdentityStage(
                new StubIdentityRegistry(ApplicationId.New(), calls)),
            new ClipboardCapturePolicyResolutionStage(
                new StubPolicyProvider(
                    new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Deny),
                    calls),
                new ClipboardCapturePolicyEvaluator()),
            new ClipboardFormatDiscoveryStage(snapshotReader),
            new ClipboardApplicationDiscoveredFormatObservationStage(observer),
            new ClipboardFormatSelectionStage());

        ClipboardFormatSelection result = await pipeline.ProcessNextAsync();

        Assert.Empty(result.Formats);
        Assert.Equal(0, snapshotReader.CallCount);
        Assert.Equal(0, observer.CallCount);
        Assert.Equal(new[] { "source", "identity", "policy" }, calls);
    }

    [Fact]
    public async Task ProcessNextAsync_MissingDurableIdentityDoesNotObserveReadSnapshot()
    {
        var calls = new List<string>();
        var queue = new ClipboardCaptureQueue();
        Assert.True(queue.TryEnqueue(Request()));
        var observer = new RecordingObserver(calls);

        var pipeline = new ClipboardCapturePipeline(
            new ClipboardCaptureSourceStage(queue, new StubSourceResolver(null, calls)),
            new ClipboardCaptureApplicationIdentityStage(
                new StubIdentityRegistry(ApplicationId.New(), calls)),
            new ClipboardCapturePolicyResolutionStage(
                new StubPolicyProvider(AllowedPolicy(), calls),
                new ClipboardCapturePolicyEvaluator()),
            new ClipboardFormatDiscoveryStage(
                new StubSnapshotReader(["Text"], calls)),
            new ClipboardApplicationDiscoveredFormatObservationStage(observer),
            new ClipboardFormatSelectionStage());

        ClipboardFormatSelection result = await pipeline.ProcessNextAsync();

        Assert.Single(result.Formats);
        Assert.Equal(0, observer.CallCount);
        Assert.Equal(new[] { "source", "policy", "formats" }, calls);
    }

    [Fact]
    public async Task ProcessNextAsync_ObserverFailurePropagatesBeforeCaptureCanContinue()
    {
        var calls = new List<string>();
        var queue = new ClipboardCaptureQueue();
        Assert.True(queue.TryEnqueue(Request()));

        var pipeline = new ClipboardCapturePipeline(
            new ClipboardCaptureSourceStage(
                queue,
                new StubSourceResolver(
                    new ClipboardSourceApplication(4242, @"C:\Apps\Failure.exe"),
                    calls)),
            new ClipboardCaptureApplicationIdentityStage(
                new StubIdentityRegistry(ApplicationId.New(), calls)),
            new ClipboardCapturePolicyResolutionStage(
                new StubPolicyProvider(AllowedPolicy(), calls),
                new ClipboardCapturePolicyEvaluator()),
            new ClipboardFormatDiscoveryStage(
                new StubSnapshotReader(["Text"], calls)),
            new ClipboardApplicationDiscoveredFormatObservationStage(
                new ThrowingObserver(calls)),
            new ClipboardFormatSelectionStage());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pipeline.ProcessNextAsync());
        Assert.Equal(new[] { "source", "identity", "policy", "formats", "observe" }, calls);
    }

    private static ClipboardCaptureRequest Request() => new(
        new EventTimeContext(
            new DateTimeOffset(2026, 9, 11, 2, 0, 0, TimeSpan.FromHours(7)),
            "Test/Zone"));

    private static ClipboardCapturePolicy AllowedPolicy() => new(
        ClipboardCapturePolicyRule.Allow,
        new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal)
        {
            ["Text"] = new(ClipboardCapturePolicyRule.Allow),
            ["HTML Format"] = new(ClipboardCapturePolicyRule.Allow),
        });

    private sealed class StubSourceResolver : IClipboardSourceApplicationResolver
    {
        private readonly ClipboardSourceApplication? _source;
        private readonly IList<string> _calls;

        public StubSourceResolver(ClipboardSourceApplication? source, IList<string> calls)
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

    private sealed class StubIdentityRegistry : IApplicationIdentityRegistry
    {
        private readonly ApplicationId _applicationId;
        private readonly IList<string> _calls;

        public StubIdentityRegistry(ApplicationId applicationId, IList<string> calls)
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

    private sealed class StubPolicyProvider : IClipboardCapturePolicyProvider
    {
        private readonly ClipboardCapturePolicy _policy;
        private readonly IList<string> _calls;

        public StubPolicyProvider(ClipboardCapturePolicy policy, IList<string> calls)
        {
            _policy = policy;
            _calls = calls;
        }

        public ValueTask<ClipboardCapturePolicySet> GetPoliciesAsync(
            ClipboardCaptureContext captureContext,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _calls.Add("policy");
            return ValueTask.FromResult(new ClipboardCapturePolicySet(_policy));
        }
    }

    private sealed class StubSnapshotReader : IClipboardFormatSnapshotReader
    {
        private readonly IReadOnlyList<string> _formats;
        private readonly IList<string> _calls;

        public StubSnapshotReader(IReadOnlyList<string> formats, IList<string> calls)
        {
            _formats = formats;
            _calls = calls;
        }

        public IClipboardContentSnapshot ReadSnapshot()
        {
            _calls.Add("formats");
            return new StubContentSnapshot(_formats);
        }
    }

    private sealed class ThrowingSnapshotReader : IClipboardFormatSnapshotReader
    {
        public int CallCount { get; private set; }

        public IClipboardContentSnapshot ReadSnapshot()
        {
            CallCount++;
            throw new InvalidOperationException("Denied capture must not read clipboard formats.");
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

    private sealed class RecordingObserver : IApplicationDiscoveredFormatObserver
    {
        private readonly IList<string> _calls;

        public RecordingObserver(IList<string> calls)
        {
            _calls = calls;
        }

        public int CallCount { get; private set; }
        public ApplicationId? ApplicationId { get; private set; }
        public IReadOnlyList<string>? FormatNames { get; private set; }
        public DateTimeOffset? ObservedAtUtc { get; private set; }

        public ValueTask ObserveAsync(
            ApplicationId applicationId,
            IReadOnlyCollection<string> formatNames,
            DateTimeOffset observedAtUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _calls.Add("observe");
            CallCount++;
            ApplicationId = applicationId;
            FormatNames = formatNames.ToArray();
            ObservedAtUtc = observedAtUtc;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingObserver : IApplicationDiscoveredFormatObserver
    {
        private readonly IList<string> _calls;

        public ThrowingObserver(IList<string> calls)
        {
            _calls = calls;
        }

        public ValueTask ObserveAsync(
            ApplicationId applicationId,
            IReadOnlyCollection<string> formatNames,
            DateTimeOffset observedAtUtc,
            CancellationToken cancellationToken = default)
        {
            _calls.Add("observe");
            throw new InvalidOperationException("Observation failure.");
        }
    }
}
