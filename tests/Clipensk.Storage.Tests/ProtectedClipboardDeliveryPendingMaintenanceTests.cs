using Clipensk.Core.Applications;
using Clipensk.Core.Clipboard;
using Clipensk.Storage.Clipboard;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedClipboardDeliveryPendingMaintenanceTests
{
    [Fact]
    public async Task PendingMaintenance_PreventsCompositionBeforePolicyOrFactory()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        Guid operationId = Guid.NewGuid();
        environment.Execute($"""
            INSERT INTO PendingStorageMaintenance (
                SingletonId, OperationId, OperationKind, CreatedAtUtc)
            VALUES (
                1,
                '{operationId:D}',
                'PolicyMutation',
                '2026-09-08T12:00:00.0000000+00:00');
            """);
        environment.Factory.Modes.Clear();
        var factory = new RecordingFactory();
        var extensions = new RepositoryClipboardCustomBinaryFileExtensionProvider(
            environment.CustomBinaryConfigurations);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ProtectedClipboardDeliveryServices.TryCreateAsync(
                environment.Session,
                factory,
                extensions,
                environment.Factory));

        Assert.Contains("maintenance", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, factory.CreateCount);
        Assert.Equal(new[] { SqliteOpenMode.ReadOnly }, environment.Factory.Modes);
    }

    private sealed class RecordingFactory : IClipboardAcceptedCaptureDeliveryFactory
    {
        public int CreateCount { get; private set; }

        public IClipboardAcceptedCaptureDelivery Create(
            IClipboardCapturePolicyProvider policyProvider,
            IClipboardAcceptedCaptureSink sink,
            IApplicationIdentityRegistry identityRegistry)
        {
            CreateCount++;
            return new Delivery();
        }
    }

    private sealed class Delivery : IClipboardAcceptedCaptureDelivery
    {
        public ValueTask<bool> ProcessNextAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(false);
    }
}
