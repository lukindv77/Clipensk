using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ClipboardCapturePolicyResolutionStageTests
{
    [Fact]
    public async Task ResolveAsync_AGroupPolicyReplacesTheGlobalPolicyInsteadOfRefiningIt()
    {
        ClipboardCaptureContext captureContext = CreateCaptureContext();
        var global = new ClipboardCapturePolicy(
            ClipboardCapturePolicyRule.Allow,
            new Dictionary<string, ClipboardFormatCapturePolicy>
            {
                ["Text"] = new(ClipboardCapturePolicyRule.Allow, 1024),
                ["HTML Format"] = new(ClipboardCapturePolicyRule.Allow),
            });
        var group = new ClipboardCapturePolicy(
            ClipboardCapturePolicyRule.Allow,
            new Dictionary<string, ClipboardFormatCapturePolicy>
            {
                ["Text"] = new(ClipboardCapturePolicyRule.Deny),
            });
        var provider = new StubPolicyProvider(new ClipboardCapturePolicySet(global, group));
        var stage = new ClipboardCapturePolicyResolutionStage(
            provider,
            new ClipboardCapturePolicyEvaluator());
        using var cancellationSource = new CancellationTokenSource();

        ClipboardCapturePolicyContext result = await stage.ResolveAsync(
            captureContext,
            cancellationSource.Token);

        Assert.Equal(captureContext, result.CaptureContext);
        Assert.Equal(ClipboardCapturePolicyRule.Allow, result.Policy.Capture);
        Assert.Equal(ClipboardCapturePolicyRule.Deny, result.Policy.Formats["Text"].Capture);
        Assert.Null(result.Policy.Formats["Text"].MaxBytes);
        Assert.False(result.Policy.Formats.ContainsKey("HTML Format"));
        Assert.Equal(captureContext, provider.LastCaptureContext);
        Assert.Equal(cancellationSource.Token, provider.LastCancellationToken);
    }

    [Fact]
    public async Task ResolveAsync_AllowsProviderToReturnOnlyGlobalPolicy()
    {
        ClipboardCaptureContext captureContext = CreateCaptureContext();
        var global = new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Deny);
        var provider = new StubPolicyProvider(new ClipboardCapturePolicySet(global));
        var stage = new ClipboardCapturePolicyResolutionStage(
            provider,
            new ClipboardCapturePolicyEvaluator());

        ClipboardCapturePolicyContext result = await stage.ResolveAsync(captureContext);

        Assert.Equal(ClipboardCapturePolicyRule.Deny, result.Policy.Capture);
    }

    private static ClipboardCaptureContext CreateCaptureContext()
    {
        var request = new ClipboardCaptureRequest(
            new EventTimeContext(
                new DateTimeOffset(2026, 9, 5, 10, 15, 30, TimeSpan.FromHours(3)),
                "Test/Zone"));

        return new ClipboardCaptureContext(
            request,
            new ClipboardSourceApplication(4242, @"C:\Apps\Source.exe"));
    }

    private sealed class StubPolicyProvider : IClipboardCapturePolicyProvider
    {
        private readonly ClipboardCapturePolicySet _policies;

        public StubPolicyProvider(ClipboardCapturePolicySet policies)
        {
            _policies = policies;
        }

        public ClipboardCaptureContext LastCaptureContext { get; private set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public ValueTask<ClipboardCapturePolicySet> GetPoliciesAsync(
            ClipboardCaptureContext captureContext,
            CancellationToken cancellationToken = default)
        {
            LastCaptureContext = captureContext;
            LastCancellationToken = cancellationToken;
            return ValueTask.FromResult(_policies);
        }
    }
}
