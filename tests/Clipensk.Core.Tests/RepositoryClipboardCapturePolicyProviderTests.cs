using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Xunit;
using DurableApplicationId = Clipensk.Core.Applications.ApplicationId;

namespace Clipensk.Core.Tests;

public sealed class RepositoryClipboardCapturePolicyProviderTests
{
    [Fact]
    public async Task GetPoliciesAsync_LoadsGlobalAndResolvedApplicationPolicyByDurableId()
    {
        var global = new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Allow);
        var application = new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Deny);
        var repository = new StubPolicyRepository(global, application);
        var provider = new RepositoryClipboardCapturePolicyProvider(repository);
        DurableApplicationId applicationId = DurableApplicationId.New();
        ClipboardCaptureContext captureContext = CreateCaptureContext(
            new ClipboardSourceApplication(4242, @"C:\Apps\Source.exe"),
            applicationId);
        using var cancellationSource = new CancellationTokenSource();

        ClipboardCapturePolicySet result = await provider.GetPoliciesAsync(
            captureContext,
            cancellationSource.Token);

        Assert.Same(global, result.GlobalPolicy);
        Assert.Same(application, result.ApplicationPolicy);
        Assert.Equal(1, repository.GlobalCallCount);
        Assert.Equal(1, repository.ApplicationCallCount);
        Assert.Equal(applicationId, repository.LastApplicationId);
        Assert.Equal(cancellationSource.Token, repository.LastGlobalCancellationToken);
        Assert.Equal(cancellationSource.Token, repository.LastApplicationCancellationToken);
    }

    [Fact]
    public async Task GetPoliciesAsync_KnownRuntimeSourceWithoutDurableIdLoadsOnlyGlobalPolicy()
    {
        var global = new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Deny);
        var repository = new StubPolicyRepository(
            global,
            new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Allow));
        var provider = new RepositoryClipboardCapturePolicyProvider(repository);
        ClipboardCaptureContext captureContext = CreateCaptureContext(
            new ClipboardSourceApplication(4242, @"C:\Apps\Source.exe"),
            applicationId: null);

        ClipboardCapturePolicySet result = await provider.GetPoliciesAsync(captureContext);

        Assert.Same(global, result.GlobalPolicy);
        Assert.Null(result.ApplicationPolicy);
        Assert.Equal(1, repository.GlobalCallCount);
        Assert.Equal(0, repository.ApplicationCallCount);
    }

    [Fact]
    public async Task GetPoliciesAsync_UnknownSourceLoadsOnlyGlobalPolicy()
    {
        var global = new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Deny);
        var repository = new StubPolicyRepository(
            global,
            new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Allow));
        var provider = new RepositoryClipboardCapturePolicyProvider(repository);
        ClipboardCaptureContext captureContext = CreateCaptureContext(
            sourceApplication: null,
            applicationId: null);

        ClipboardCapturePolicySet result = await provider.GetPoliciesAsync(captureContext);

        Assert.Same(global, result.GlobalPolicy);
        Assert.Null(result.ApplicationPolicy);
        Assert.Equal(1, repository.GlobalCallCount);
        Assert.Equal(0, repository.ApplicationCallCount);
    }

    private static ClipboardCaptureContext CreateCaptureContext(
        ClipboardSourceApplication? sourceApplication,
        DurableApplicationId? applicationId)
    {
        var request = new ClipboardCaptureRequest(
            new EventTimeContext(
                new DateTimeOffset(2026, 9, 5, 10, 15, 30, TimeSpan.FromHours(3)),
                "Test/Zone"));

        return new ClipboardCaptureContext(request, sourceApplication, applicationId);
    }

    private sealed class StubPolicyRepository : IClipboardCapturePolicyRepository
    {
        private readonly ClipboardCapturePolicy _globalPolicy;
        private readonly ClipboardCapturePolicy? _applicationPolicy;

        public StubPolicyRepository(
            ClipboardCapturePolicy globalPolicy,
            ClipboardCapturePolicy? applicationPolicy)
        {
            _globalPolicy = globalPolicy;
            _applicationPolicy = applicationPolicy;
        }

        public int GlobalCallCount { get; private set; }

        public int ApplicationCallCount { get; private set; }

        public DurableApplicationId? LastApplicationId { get; private set; }

        public CancellationToken LastGlobalCancellationToken { get; private set; }

        public CancellationToken LastApplicationCancellationToken { get; private set; }

        public ValueTask<ClipboardCapturePolicy> GetGlobalPolicyAsync(
            CancellationToken cancellationToken = default)
        {
            GlobalCallCount++;
            LastGlobalCancellationToken = cancellationToken;
            return ValueTask.FromResult(_globalPolicy);
        }

        public ValueTask<ClipboardCapturePolicy?> GetApplicationPolicyAsync(
            DurableApplicationId applicationId,
            CancellationToken cancellationToken = default)
        {
            ApplicationCallCount++;
            LastApplicationId = applicationId;
            LastApplicationCancellationToken = cancellationToken;
            return ValueTask.FromResult(_applicationPolicy);
        }
    }
}
