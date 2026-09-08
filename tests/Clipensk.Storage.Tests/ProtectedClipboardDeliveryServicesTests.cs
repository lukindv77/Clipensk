using Clipensk.Core.Applications;
using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Clipensk.Storage.Clipboard;
using Clipensk.Storage.History;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedClipboardDeliveryServicesTests
{
    [Fact]
    public async Task UnconfiguredPolicy_DoesNotConstructOrProcessDelivery()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var factory = new RecordingFactory();
        var extensions = new NoExtensionRequests();
        environment.Factory.Modes.Clear();

        var services = await ProtectedClipboardDeliveryServices.TryCreateAsync(
            environment.Session, factory, extensions, environment.Factory);

        Assert.Null(services);
        Assert.Equal(new[] { SqliteOpenMode.ReadOnly, SqliteOpenMode.ReadOnly }, environment.Factory.Modes);
        Assert.Equal(0, factory.CreateCount);
        Assert.Equal(0, factory.ProcessCount);
        Assert.Equal(0, extensions.CallCount);
    }

    [Theory]
    [InlineData(ClipboardCapturePolicyRule.Allow)]
    [InlineData(ClipboardCapturePolicyRule.Deny)]
    public async Task ConfiguredPolicy_ComposesOneSessionWithoutStartingProcessing(ClipboardCapturePolicyRule rule)
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(new ClipboardCapturePolicy(rule,
            new Dictionary<string, ClipboardFormatCapturePolicy> { ["Text"] = new(ClipboardCapturePolicyRule.Allow, 4096) }));
        var factory = new RecordingFactory();
        var extensions = new NoExtensionRequests();
        environment.Factory.Modes.Clear();

        var services = await ProtectedClipboardDeliveryServices.TryCreateAsync(
            environment.Session, factory, extensions, environment.Factory);

        Assert.NotNull(services);
        Assert.Equal(new[] { SqliteOpenMode.ReadOnly, SqliteOpenMode.ReadOnly }, environment.Factory.Modes);
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(0, factory.ProcessCount);
        Assert.Equal(0, extensions.CallCount);
        Assert.Same(services.CaptureServices.PolicyProvider, factory.PolicyProvider);
        Assert.Same(services.CaptureServices.ApplicationIdentityRegistry, factory.IdentityRegistry);
        Assert.Same(services.HistoryServices.HistorySink, factory.Sink);
        ClipboardCapturePolicySet policies = await factory.PolicyProvider!.GetPoliciesAsync(Context());
        Assert.Equal(rule, policies.GlobalPolicy.Capture);
        Assert.Equal(4096, policies.GlobalPolicy.Formats["Text"].MaxBytes);
        Assert.Null(policies.ApplicationPolicy);
        Assert.Equal(2, environment.Factory.Modes.Count);
    }

    [Fact]
    public async Task ComposedIdentityAndPolicyRepositoriesResolveDurableApplicationOverrides()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Deny));
        var factory = new RecordingFactory();
        var services = await ProtectedClipboardDeliveryServices.TryCreateAsync(
            environment.Session, factory, new NoExtensionRequests(), environment.Factory);
        Assert.NotNull(services);
        ApplicationIdentityResolution? identity = await factory.IdentityRegistry!.ResolveOrCreateAsync(
            new ApplicationIdentityObservation(null, @"C:\Apps\Example.exe"));
        Assert.NotNull(identity);
        await services.CaptureServices.PolicyRepository.SetApplicationPolicyAsync(identity.ApplicationId,
            new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Allow));
        ClipboardCapturePolicySet policies = await factory.PolicyProvider!.GetPoliciesAsync(
            Context() with { SourceApplicationId = identity.ApplicationId });
        Assert.Equal(ClipboardCapturePolicyRule.Deny, policies.GlobalPolicy.Capture);
        Assert.Equal(ClipboardCapturePolicyRule.Allow, policies.ApplicationPolicy!.Capture);
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("lock")]
    [InlineData("dispose")]
    public async Task CancelledCreation_DoesNotOpenOrInvokeFactory(string cause)
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        if (cause == "caller") cancellation.Cancel();
        if (cause == "lock") Assert.True(environment.Lifecycle.TryBeginLock());
        if (cause == "dispose") environment.Session.Dispose();
        var factory = new RecordingFactory();
        environment.Factory.Modes.Clear();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ProtectedClipboardDeliveryServices.TryCreateAsync(environment.Session,
                factory, new NoExtensionRequests(), environment.Factory, cancellation.Token));
        Assert.Empty(environment.Factory.Modes);
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task MissingExtensionProvider_IsNotReplacedWithAHiddenFallback()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Factory.Modes.Clear();
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await ProtectedClipboardDeliveryServices.TryCreateAsync(
                environment.Session, new RecordingFactory(), null!, environment.Factory));
        Assert.Empty(environment.Factory.Modes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationInsideFactory_DoesNotPublishStaleServices(bool lockSession)
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Allow));
        using var cancellation = new CancellationTokenSource();
        var factory = new RecordingFactory
        {
            OnCreate = () =>
            {
                if (lockSession) Assert.True(environment.Lifecycle.TryBeginLock());
                else cancellation.Cancel();
            },
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ProtectedClipboardDeliveryServices.TryCreateAsync(
                environment.Session, factory, new NoExtensionRequests(), environment.Factory, cancellation.Token));
        Assert.Equal(0, factory.ProcessCount);
    }

    [Fact]
    public async Task MalformedPolicy_IsAnErrorRatherThanUnconfiguredOrDefault()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Execute("PRAGMA ignore_check_constraints = ON; INSERT INTO GlobalCapturePolicy VALUES (1, 'Inherit');");
        var factory = new RecordingFactory();
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await ProtectedClipboardDeliveryServices.TryCreateAsync(
                environment.Session, factory, new NoExtensionRequests(), environment.Factory));
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task CreationTokenDoesNotBecomeTheLifetimeOfReturnedServices()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Allow));
        using var creationCancellation = new CancellationTokenSource();
        var factory = new RecordingFactory();
        var services = await ProtectedClipboardDeliveryServices.TryCreateAsync(
            environment.Session, factory, new NoExtensionRequests(), environment.Factory, creationCancellation.Token);
        Assert.NotNull(services);
        creationCancellation.Cancel();
        Assert.False(await services.Delivery.ProcessNextAsync());
        Assert.Equal(1, factory.ProcessCount);
        using var callCancellation = new CancellationTokenSource();
        callCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await services.Delivery.ProcessNextAsync(callCancellation.Token));
        Assert.Equal(1, factory.ProcessCount);
        environment.ReopenSession();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await services.Delivery.ProcessNextAsync());
        Assert.Equal(1, factory.ProcessCount);
    }

    [Fact]
    public async Task ExplicitProcessingUsesComposedSinkAndPreservesSuccessAfterCommitAndLock()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Allow));
        var route = new ClipboardContentReaderRoute(new ClipboardSelectedFormat("Text", null), ClipboardContentReaderKind.Text);
        var factory = new RecordingFactory
        {
            NextCapture = new ClipboardAcceptedCapture(Context(), [new ClipboardCapturedTextContent(route, "saved", 5)]),
            AfterStore = () => Assert.True(environment.Lifecycle.TryBeginLock()),
        };
        var extensions = new NoExtensionRequests();
        var services = await ProtectedClipboardDeliveryServices.TryCreateAsync(
            environment.Session, factory, extensions, environment.Factory);
        Assert.NotNull(services);
        Assert.Equal(0, factory.ProcessCount);
        Assert.True(await services.Delivery.ProcessNextAsync());
        Assert.Equal(1, environment.Scalar("SELECT COUNT(*) FROM ClipboardHistoryPayload WHERE InlineCanonicalText = 'saved';"));
        Assert.Equal(0, extensions.CallCount);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await services.Delivery.ProcessNextAsync());
        Assert.Equal(1, factory.ProcessCount);
    }

    private static ClipboardCaptureContext Context() => new(
        new ClipboardCaptureRequest(new EventTimeContext(
            new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero), "UTC")), null);

    private sealed class RecordingFactory : IClipboardAcceptedCaptureDeliveryFactory, IClipboardAcceptedCaptureDelivery
    {
        public int CreateCount { get; private set; }
        public int ProcessCount { get; private set; }
        public IClipboardCapturePolicyProvider? PolicyProvider { get; private set; }
        public IApplicationIdentityRegistry? IdentityRegistry { get; private set; }
        public IClipboardAcceptedCaptureSink? Sink { get; private set; }
        public Action? OnCreate { get; init; }
        public Action? AfterStore { get; init; }
        public ClipboardAcceptedCapture? NextCapture { get; init; }

        public IClipboardAcceptedCaptureDelivery Create(IClipboardCapturePolicyProvider policyProvider,
            IClipboardAcceptedCaptureSink sink, IApplicationIdentityRegistry identityRegistry)
        {
            CreateCount++;
            PolicyProvider = policyProvider;
            Sink = sink;
            IdentityRegistry = identityRegistry;
            OnCreate?.Invoke();
            return this;
        }

        public async ValueTask<bool> ProcessNextAsync(CancellationToken cancellationToken = default)
        {
            ProcessCount++;
            cancellationToken.ThrowIfCancellationRequested();
            if (NextCapture is null) return false;
            await Sink!.StoreAsync(NextCapture, cancellationToken);
            AfterStore?.Invoke();
            return true;
        }
    }

    private sealed class NoExtensionRequests : IClipboardCustomBinaryFileExtensionProvider
    {
        public int CallCount { get; private set; }
        public ValueTask<string> GetExtensionAsync(string formatName, CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new InvalidOperationException("No custom extension should be requested by composition or text capture.");
        }
    }
}
