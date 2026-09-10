using Clipensk.Core.Application;
using Clipensk.Core.Clipboard;
using Clipensk.Core.Storage;
using Clipensk.Storage.Clipboard;
using Clipensk.Windows;

namespace Clipensk.App;

public partial class App
{
    private readonly object _policyMaintenanceRecoveryGate = new();
    private Task _policyMaintenanceRecoveryTask = Task.CompletedTask;
    private ProtectedStorageSessionLease? _policyMaintenanceRecoverySession;

    private void RequestPolicyMaintenanceRecovery(
        JournalWindow window,
        ResidentWindowsHost host)
    {
        ProtectedApplicationLifecycle? lifecycle = _lifecycle;
        if (lifecycle is null ||
            !window.TryGetActiveProtectedStorageSession(out ProtectedStorageSessionLease? session) ||
            session is null ||
            !IsCurrentProtectedStorageSession(host, window, lifecycle, session))
        {
            return;
        }

        lock (_policyMaintenanceRecoveryGate)
        {
            if (ReferenceEquals(_policyMaintenanceRecoverySession, session) &&
                !_policyMaintenanceRecoveryTask.IsCompleted)
            {
                return;
            }

            _policyMaintenanceRecoverySession = session;
            _policyMaintenanceRecoveryTask = RecoverPolicyMaintenanceAndResumeRuntimeAsync(
                window,
                host,
                lifecycle,
                session);
        }
    }

    private async Task RecoverPolicyMaintenanceAndResumeRuntimeAsync(
        JournalWindow window,
        ResidentWindowsHost host,
        ProtectedApplicationLifecycle lifecycle,
        ProtectedStorageSessionLease session)
    {
        long? suspensionOwner;
        try
        {
            suspensionOwner = await TryQuiesceClipboardRuntimeAsync(
                window,
                host,
                lifecycle,
                session,
                session.CancellationToken);
            if (!suspensionOwner.HasValue)
            {
                return;
            }

            DateOnly deletionDate = DateOnly.FromDateTime(DateTime.Now);
            await Task.Run(
                () => new ProtectedPolicyMaintenanceResumeDispatcher(session)
                    .ResumeAsync(deletionDate, session.CancellationToken),
                session.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            // Pending maintenance is a fail-closed runtime boundary. Do not release the
            // suspension after any continuation failure; a later lock/unlock may retry it.
            return;
        }

        TryResumeClipboardRuntimeAfterMaintenance(
            window,
            host,
            lifecycle,
            session,
            suspensionOwner.Value);
    }

    internal async Task<bool> TryApplyGlobalCapturePolicyChangeAsync(
        ProtectedStorageSessionLease session,
        global::Clipensk.Core.Clipboard.ClipboardCapturePolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(policy);

        JournalWindow? window = _window;
        ResidentWindowsHost? host = _residentWindowsHost;
        ProtectedApplicationLifecycle? lifecycle = _lifecycle;
        if (window is null || host is null || lifecycle is null)
        {
            return false;
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
            return false;
        }

        if (!IsCurrentProtectedStorageSession(host, window, lifecycle, session))
        {
            return false;
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
            return false;
        }

        if (!suspensionOwner.HasValue)
        {
            return false;
        }

        try
        {
            DateOnly deletionDate = DateOnly.FromDateTime(DateTime.Now);
            await Task.Run(
                async () =>
                {
                    await new ProtectedCurrentPolicyMaintenanceService(session)
                        .ApplyAsync(policy, token)
                        .ConfigureAwait(false);
                    await new ProtectedGlobalPolicyMaintenanceResumeCoordinator(session)
                        .ResumeAsync(deletionDate, token)
                        .ConfigureAwait(false);
                },
                token);

            return TryResumeClipboardRuntimeAfterMaintenance(
                window,
                host,
                lifecycle,
                session,
                suspensionOwner.Value);
        }
        catch
        {
            bool markerExists = true;
            try
            {
                markerExists = (await new global::Clipensk.Storage.Clipboard.SqlitePendingPolicyMaintenanceRepository(session)
                        .ReadAsync(session.CancellationToken)
                        .ConfigureAwait(false)) is not null;
            }
            catch
            {
                // If durable state cannot be established, remain fail-closed. Releasing the
                // listener while a continuation may be pending would violate the maintenance
                // boundary.
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

            return false;
        }
    }

    internal async Task<bool> TryApplyApplicationCapturePolicyChangeAsync(
        ProtectedStorageSessionLease session,
        global::Clipensk.Core.Applications.ApplicationId applicationId,
        global::Clipensk.Core.Clipboard.ClipboardCapturePolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(applicationId);
        ArgumentNullException.ThrowIfNull(policy);

        JournalWindow? window = _window;
        ResidentWindowsHost? host = _residentWindowsHost;
        ProtectedApplicationLifecycle? lifecycle = _lifecycle;
        if (window is null || host is null || lifecycle is null)
        {
            return false;
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
            return false;
        }

        if (!IsCurrentProtectedStorageSession(host, window, lifecycle, session))
        {
            return false;
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
            return false;
        }

        if (!suspensionOwner.HasValue)
        {
            return false;
        }

        try
        {
            DateOnly deletionDate = DateOnly.FromDateTime(DateTime.Now);
            await Task.Run(
                async () =>
                {
                    await new ProtectedCurrentApplicationPolicyMaintenanceService(session)
                        .ApplyAsync(applicationId, policy, token)
                        .ConfigureAwait(false);
                    await new ProtectedApplicationPolicyMaintenanceResumeCoordinator(session)
                        .ResumeAsync(deletionDate, token)
                        .ConfigureAwait(false);
                },
                token);

            return TryResumeClipboardRuntimeAfterMaintenance(
                window,
                host,
                lifecycle,
                session,
                suspensionOwner.Value);
        }
        catch
        {
            bool markerExists = true;
            try
            {
                markerExists = (await new global::Clipensk.Storage.Clipboard.SqlitePendingPolicyMaintenanceRepository(session)
                        .ReadAsync(session.CancellationToken)
                        .ConfigureAwait(false)) is not null;
            }
            catch
            {
                // If durable state cannot be established, remain fail-closed. Releasing the
                // listener while a continuation may be pending would violate the maintenance
                // boundary.
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

            return false;
        }
    }
}
