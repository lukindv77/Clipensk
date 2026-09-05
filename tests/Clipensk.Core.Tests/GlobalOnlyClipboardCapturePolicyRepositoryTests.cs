using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Xunit;
using DurableApplicationId = Clipensk.Core.Applications.ApplicationId;

namespace Clipensk.Core.Tests;

public sealed class GlobalOnlyClipboardCapturePolicyRepositoryTests
{
    [Fact]
    public async Task GetGlobalPolicyAsync_ReturnsConfiguredPolicy()
    {
        var globalPolicy = new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Deny);
        var repository = new GlobalOnlyClipboardCapturePolicyRepository(globalPolicy);

        ClipboardCapturePolicy result = await repository.GetGlobalPolicyAsync();

        Assert.Same(globalPolicy, result);
    }

    [Fact]
    public async Task GetApplicationPolicyAsync_ReturnsNoOverrideForDurableApplicationId()
    {
        var globalPolicy = new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Allow);
        var repository = new GlobalOnlyClipboardCapturePolicyRepository(globalPolicy);

        ClipboardCapturePolicy? result = await repository.GetApplicationPolicyAsync(
            DurableApplicationId.New());

        Assert.Null(result);
    }

    [Fact]
    public async Task RepositoryOperations_HonorCancellation()
    {
        var repository = new GlobalOnlyClipboardCapturePolicyRepository(
            new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Deny));
        DurableApplicationId applicationId = DurableApplicationId.New();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await repository.GetGlobalPolicyAsync(cancellation.Token);
        });
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await repository.GetApplicationPolicyAsync(applicationId, cancellation.Token);
        });
    }

    [Fact]
    public async Task RepositoryProvider_KeepsKnownRuntimeSourceWithoutDurableIdOnGlobalOnlyPolicy()
    {
        var globalPolicy = new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Allow);
        var provider = new RepositoryClipboardCapturePolicyProvider(
            new GlobalOnlyClipboardCapturePolicyRepository(globalPolicy));
        var captureContext = new ClipboardCaptureContext(
            new ClipboardCaptureRequest(
                new EventTimeContext(
                    new DateTimeOffset(2026, 9, 5, 16, 30, 0, TimeSpan.FromHours(7)),
                    "Test/Zone")),
            new ClipboardSourceApplication(4242, @"C:\Apps\Source.exe"));

        ClipboardCapturePolicySet policies = await provider.GetPoliciesAsync(captureContext);

        Assert.Same(globalPolicy, policies.GlobalPolicy);
        Assert.Null(policies.ApplicationPolicy);
    }
}
