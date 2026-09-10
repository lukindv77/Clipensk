using Clipensk.Core.Applications;
using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Clipensk.Storage.Clipboard;
using Clipensk.Storage.History;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class PendingPolicyMaintenanceDeliveryTests
{
    [Fact]
    public async Task PendingMarker_BlocksCompositionBeforePolicyReadOrFactoryCreation()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Allow));
        environment.Execute("""
            INSERT INTO PendingPolicyMaintenance (
                SingletonId, OperationId, OperationKind, StateJson, CreatedAtUtc, UpdatedAtUtc)
            VALUES (
                1,
                '11111111-1111-1111-1111-111111111111',
                'GlobalCapturePolicyChange',
                '{}',
                '2026-09-08T00:00:00.0000000+00:00',
                '2026-09-08T00:00:00.0000000+00:00');
            """);
        var factory = new RecordingFactory();
        environment.Factory.Modes.Clear();

        await Assert.ThrowsAsync<PendingPolicyMaintenanceException>(async () =>
            await ProtectedClipboardDeliveryServices.TryCreateAsync(
                environment.Session,
                factory,
                new NoExtensionRequests(),
                environment.Factory));

        Assert.Equal(new[] { SqliteOpenMode.ReadOnly }, environment.Factory.Modes);
        Assert.Equal(0, factory.CreateCount);
    }

    private sealed class RecordingFactory : IClipboardAcceptedCaptureDeliveryFactory
    {
        public int CreateCount { get; private set; }

        public IClipboardAcceptedCaptureDelivery Create(
            IClipboardCapturePolicyProvider policyProvider,
            IClipboardAcceptedCaptureSink sink,
            IApplicationIdentityRegistry identityRegistry,
            IApplicationDiscoveredFormatObserver discoveredFormatObserver)
        {
            CreateCount++;
            throw new InvalidOperationException("Factory must not run while maintenance is pending.");
        }
    }

    private sealed class NoExtensionRequests : IClipboardCustomBinaryFileExtensionProvider
    {
        public ValueTask<string> GetExtensionAsync(
            string formatName,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Extension lookup must not run during composition.");
    }
}
