using Clipensk.Core.Application;
using Clipensk.Core.Storage;
using Clipensk.Storage.Clipboard;
using Clipensk.Windows;

namespace Clipensk.App;

public partial class App
{
    internal async Task<bool> TryApplyApplicationCapturePolicyChangeAsync(
        ProtectedStorageSessionLease session,
        global::Clipensk.Core.Applications.ApplicationId applicationId,
        global::Clipensk.Core.Clipboard.ClipboardCapturePolicy policy,
        IReadOnlyList<ApplicationCustomBinaryFormatConfiguration> customBinaryConfigurations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(applicationId);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(customBinaryConfigurations);

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
                        .ApplyAsync(
                            applicationId,
                            policy,
                            customBinaryConfigurations,
                            token)
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
                markerExists = (await new SqlitePendingPolicyMaintenanceRepository(session)
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
