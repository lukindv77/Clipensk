using Clipensk.Core.Application;
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
                () => new ProtectedGlobalPolicyMaintenanceResumeCoordinator(session)
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
}
