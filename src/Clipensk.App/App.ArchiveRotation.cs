using Clipensk.Core.Application;
using Clipensk.Core.Settings;
using Clipensk.Storage.Databases;
using Clipensk.Windows;

namespace Clipensk.App;

public partial class App
{
    /// <summary>
    /// Runs one Archive rotation on explicit user request, on exactly the runtime boundary the
    /// startup sequence uses: any pending startup recovery is awaited first, clipboard capture is
    /// quiesced before the durable operation begins, and the runtime is only resumed once no
    /// pending rotation marker is left behind.
    ///
    /// Returns <c>null</c> when the rotation could not be started at all — no current protected
    /// session, or the runtime could not be quiesced. A genuine rotation failure propagates, so the
    /// caller can distinguish "not started" from "started and failed".
    /// </summary>
    internal async Task<ArchiveRotationRunResult?> TryRunArchiveRotationAsync(
        ProtectedStorageSessionLease session,
        ArchiveRotationSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(settings);

        JournalWindow? window = _window;
        ResidentWindowsHost? host = _residentWindowsHost;
        ProtectedApplicationLifecycle? lifecycle = _lifecycle;
        if (window is null || host is null || lifecycle is null)
        {
            return null;
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            session.CancellationToken,
            cancellationToken);
        CancellationToken token = linked.Token;

        Task recoveryTask;
        lock (_policyMaintenanceRecoveryGate)
        {
            recoveryTask = ReferenceEquals(_policyMaintenanceRecoverySession, session)
                ? _policyMaintenanceRecoveryTask
                : Task.CompletedTask;
        }

        try
        {
            await recoveryTask.WaitAsync(token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        if (!IsCurrentProtectedStorageSession(host, window, lifecycle, session))
        {
            return null;
        }

        long? suspensionOwner;
        try
        {
            suspensionOwner = await TryQuiesceClipboardRuntimeAsync(
                window,
                host,
                lifecycle,
                session,
                token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        if (!suspensionOwner.HasValue)
        {
            return null;
        }

        try
        {
            DateOnly currentCalendarDate = DateOnly.FromDateTime(DateTime.Now);
            ArchiveRotationRunResult result = await Task.Run(
                () => new ProtectedArchiveRotationStartService(session)
                    .StartAndCompleteAsync(settings, currentCalendarDate, token),
                token);

            TryResumeClipboardRuntimeAfterMaintenance(
                window,
                host,
                lifecycle,
                session,
                suspensionOwner.Value);
            return result;
        }
        catch
        {
            bool markerExists = true;
            try
            {
                markerExists = (await new SqlitePendingArchiveRotationRepository(session)
                        .ReadAsync(session.CancellationToken)
                        .ConfigureAwait(false)) is not null;
            }
            catch
            {
                // If durable state cannot be established, remain fail-closed. Resuming capture
                // while a rotation may still be pending would violate the maintenance boundary.
            }

            if (!markerExists)
            {
                TryResumeClipboardRuntimeAfterMaintenance(
                    window,
                    host,
                    lifecycle,
                    session,
                    suspensionOwner.Value);
            }

            throw;
        }
    }
}
