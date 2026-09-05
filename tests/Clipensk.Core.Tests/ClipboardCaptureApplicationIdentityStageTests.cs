using Clipensk.Core.Applications;
using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Xunit;
using DurableApplicationId = Clipensk.Core.Applications.ApplicationId;

namespace Clipensk.Core.Tests;

public sealed class ClipboardCaptureApplicationIdentityStageTests
{
    [Fact]
    public async Task ResolveAsync_UsesAumidAndExecutablePathEvidenceAndStoresDurableId()
    {
        DurableApplicationId applicationId = DurableApplicationId.New();
        var registry = new StubRegistry(new ApplicationIdentityResolution(
            applicationId,
            ApplicationIdentityResolutionBasis.PackagedApplicationUserModelId,
            wasCreated: false));
        var stage = new ClipboardCaptureApplicationIdentityStage(registry);
        ClipboardCaptureContext context = CreateContext(
            new ClipboardSourceApplication(
                4242,
                @"C:\Apps\Contoso.exe",
                "Contoso.Package_123!App"));

        ClipboardCaptureContext resolved = await stage.ResolveAsync(context);

        Assert.Equal(applicationId, resolved.SourceApplicationId);
        Assert.Equal(context.Request, resolved.Request);
        Assert.Equal(context.SourceApplication, resolved.SourceApplication);
        Assert.Equal(1, registry.CallCount);
        Assert.Equal("Contoso.Package_123!App", registry.LastObservation?.ApplicationUserModelId);
        Assert.Equal(@"C:\Apps\Contoso.exe", registry.LastObservation?.ExecutablePath);
    }

    [Fact]
    public async Task ResolveAsync_ProcessIdWithoutDurableEvidenceDoesNotCallRegistry()
    {
        var registry = new StubRegistry(new ApplicationIdentityResolution(
            DurableApplicationId.New(),
            ApplicationIdentityResolutionBasis.ExecutablePathAlias,
            wasCreated: true));
        var stage = new ClipboardCaptureApplicationIdentityStage(registry);
        ClipboardCaptureContext context = CreateContext(
            new ClipboardSourceApplication(4242, ExecutablePath: null, ApplicationUserModelId: null));

        ClipboardCaptureContext resolved = await stage.ResolveAsync(context);

        Assert.Null(resolved.SourceApplicationId);
        Assert.Equal(0, registry.CallCount);
    }

    [Fact]
    public async Task ResolveAsync_AlreadyResolvedContextDoesNotCallRegistryAgain()
    {
        DurableApplicationId applicationId = DurableApplicationId.New();
        var registry = new StubRegistry(null);
        var stage = new ClipboardCaptureApplicationIdentityStage(registry);
        ClipboardCaptureContext context = CreateContext(
            new ClipboardSourceApplication(4242, @"C:\Apps\Contoso.exe"),
            applicationId);

        ClipboardCaptureContext resolved = await stage.ResolveAsync(context);

        Assert.Same(applicationId, resolved.SourceApplicationId);
        Assert.Equal(0, registry.CallCount);
    }

    [Fact]
    public async Task ResolveAsync_NullRegistryResolutionLeavesContextUnresolved()
    {
        var registry = new StubRegistry(null);
        var stage = new ClipboardCaptureApplicationIdentityStage(registry);
        ClipboardCaptureContext context = CreateContext(
            new ClipboardSourceApplication(4242, @"C:\Apps\Contoso.exe"));

        ClipboardCaptureContext resolved = await stage.ResolveAsync(context);

        Assert.Null(resolved.SourceApplicationId);
        Assert.Equal(1, registry.CallCount);
    }

    [Fact]
    public async Task ResolveAsync_HonorsCancellationBeforeRegistryAccess()
    {
        var registry = new StubRegistry(null);
        var stage = new ClipboardCaptureApplicationIdentityStage(registry);
        ClipboardCaptureContext context = CreateContext(
            new ClipboardSourceApplication(4242, @"C:\Apps\Contoso.exe"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await stage.ResolveAsync(context, cancellation.Token));

        Assert.Equal(0, registry.CallCount);
    }

    private static ClipboardCaptureContext CreateContext(
        ClipboardSourceApplication? sourceApplication,
        DurableApplicationId? applicationId = null)
    {
        return new ClipboardCaptureContext(
            new ClipboardCaptureRequest(
                new EventTimeContext(
                    new DateTimeOffset(2026, 9, 6, 1, 0, 0, TimeSpan.FromHours(7)),
                    "Test/Zone")),
            sourceApplication,
            applicationId);
    }

    private sealed class StubRegistry : IApplicationIdentityRegistry
    {
        private readonly ApplicationIdentityResolution? _resolution;

        public StubRegistry(ApplicationIdentityResolution? resolution)
        {
            _resolution = resolution;
        }

        public int CallCount { get; private set; }

        public ApplicationIdentityObservation? LastObservation { get; private set; }

        public ValueTask<ApplicationIdentityResolution?> ResolveOrCreateAsync(
            ApplicationIdentityObservation observation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastObservation = observation;
            return ValueTask.FromResult(_resolution);
        }
    }
}
