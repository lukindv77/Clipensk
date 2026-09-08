using Clipensk.Core.Clipboard;
using Clipensk.Core.Storage;
using Clipensk.Storage.History;
using Clipensk.Storage.Sqlite;

namespace Clipensk.Storage.Clipboard;

/// <summary>Composes delivery from the configured policy belonging to one active storage session.</summary>
public sealed class ProtectedClipboardDeliveryServices
{
    private ProtectedClipboardDeliveryServices(
        ProtectedClipboardCaptureServices captureServices,
        ProtectedClipboardHistoryServices historyServices,
        IClipboardAcceptedCaptureDelivery delivery)
    {
        CaptureServices = captureServices;
        HistoryServices = historyServices;
        Delivery = delivery;
    }

    public ProtectedClipboardCaptureServices CaptureServices { get; }
    public ProtectedClipboardHistoryServices HistoryServices { get; }
    public IClipboardAcceptedCaptureDelivery Delivery { get; }

    /// <summary>
    /// Returns null only when the persisted global policy is not configured.
    /// Pending durable maintenance is fail-closed and prevents graph construction.
    /// Remaining graph construction is lazy and does not start processing.
    /// </summary>
    public static async ValueTask<ProtectedClipboardDeliveryServices?> TryCreateAsync(
        ProtectedStorageSessionLease session,
        IClipboardAcceptedCaptureDeliveryFactory deliveryFactory,
        IClipboardCustomBinaryFileExtensionProvider customBinaryExtensionProvider,
        IKeyedSqliteConnectionFactory? connectionFactory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(deliveryFactory);
        ArgumentNullException.ThrowIfNull(customBinaryExtensionProvider);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            session.CancellationToken, cancellationToken);
        CancellationToken token = linked.Token;
        EnsureActive(session, token);

        var pendingMaintenance = new SqlitePendingStorageMaintenanceRepository(session, connectionFactory);
        if (await pendingMaintenance.HasPendingAsync(token).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Clipboard capture cannot start while protected storage maintenance is pending.");
        }

        EnsureActive(session, token);
        var globalPolicies = new SqliteGlobalClipboardCapturePolicyRepository(session, connectionFactory);
        ClipboardCapturePolicy? policy = await globalPolicies.ReadAsync(token).ConfigureAwait(false);
        EnsureActive(session, token);
        if (policy is null) return null;

        ProtectedClipboardCaptureServices capture = ProtectedClipboardCaptureServices.Create(
            session, policy, connectionFactory);
        ProtectedClipboardHistoryServices history = ProtectedClipboardHistoryServices.Create(
            session, customBinaryExtensionProvider, connectionFactory);
        EnsureActive(session, token);
        IClipboardAcceptedCaptureDelivery inner = deliveryFactory.Create(
            capture.PolicyProvider, history.HistorySink, capture.ApplicationIdentityRegistry)
            ?? throw new InvalidOperationException("Delivery factory returned no delivery graph.");
        EnsureActive(session, token);

        return new ProtectedClipboardDeliveryServices(
            capture, history, new ProtectedClipboardAcceptedCaptureDelivery(inner, session));
    }

    private static void EnsureActive(ProtectedStorageSessionLease session, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!session.IsActive) throw new OperationCanceledException(session.CancellationToken);
    }
}
