using Clipensk.Core.Application;
using Clipensk.Core.Clipboard;
using Clipensk.Core.Settings;
using Clipensk.Core.Storage;
using Clipensk.Storage.Clipboard;
using Clipensk.Storage.Databases;
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

            DateOnly currentCalendarDate = DateOnly.FromDateTime(DateTime.Now);
            ArchiveRotationSettings? rotationSettings = window.ArchiveRotationSettings;
            int trashRetentionDays = window.TrashRetentionDays;
            await Task.Run(
                () => new ProtectedStorageStartupRecoveryCoordinator(session)
                    .RunAsync(
                        currentCalendarDate,
                        rotationSettings,
                        trashRetentionDays,
                        session.CancellationToken),
                session.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            // Archive split, Archive rotation and policy maintenance are all fail-closed runtime
            // boundaries. Do not release the suspension after any continuation failure; a later
            // lock/unlock or the explicit Maintenance recovery UI may retry the durable operation.
            return;
        }

        TryResumeClipboardRuntimeAfterMaintenance(
            window,
            host,
            lifecycle,
            session,
            suspensionOwner.Value);
    }

    internal Task<bool> TryApplyGlobalCapturePolicyChangeAsync(
        ProtectedStorageSessionLease session,
        global::Clipensk.Core.Clipboard.ClipboardCapturePolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(policy);

        // Editing the global policy only affects future capture; saved history is kept
        // (docs/APPLICATION_GROUP_PROTOCOL.md §3).
        return TryRunPolicyChangeWithQuiescedRuntimeAsync(
            session,
            token => new ProtectedCapturePolicyPublishService(session)
                .PublishGlobalPolicyAsync(policy, token),
            cancellationToken);
    }

    /// <summary>
    /// Publishes an edited user group policy. It applies to future capture of every application in
    /// the group; saved history is kept (docs/APPLICATION_GROUP_PROTOCOL.md §6).
    /// </summary>
    internal Task<bool> TryApplyGroupCapturePolicyChangeAsync(
        ProtectedStorageSessionLease session,
        global::Clipensk.Core.Applications.ApplicationGroupId groupId,
        global::Clipensk.Core.Clipboard.ClipboardCapturePolicy policy,
        IReadOnlyList<ApplicationCustomBinaryFormatConfiguration> customBinaryConfigurations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(groupId);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(customBinaryConfigurations);

        return TryRunPolicyChangeWithQuiescedRuntimeAsync(
            session,
            token => new ProtectedCapturePolicyPublishService(session)
                .PublishGroupPolicyAsync(groupId, policy, customBinaryConfigurations, token),
            cancellationToken);
    }

    /// <summary>
    /// Moves an application into a user group as the user confirmed it from
    /// <paramref name="confirmedPreview"/>, purging the history the target group disallows
    /// (docs/APPLICATION_GROUP_PROTOCOL.md §5). Capture stays quiesced for the whole operation; if a
    /// later phase fails, the durable marker keeps it suspended until recovery finishes the purge.
    /// </summary>
    internal async Task<ApplicationGroupMoveOutcome> TryMoveApplicationToGroupAsync(
        ProtectedStorageSessionLease session,
        ApplicationGroupMoveRequest request,
        ApplicationGroupMovePreview confirmedPreview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(confirmedPreview);

        ApplicationHistoryPurgeResult? result = null;
        ApplicationHistoryPurgeIncompleteException? incomplete = null;
        ApplicationGroupMoveOutcomeKind? refusal = null;
        await TryRunPolicyChangeWithQuiescedRuntimeAsync(
            session,
            async token =>
            {
                try
                {
                    result = await new ProtectedApplicationHistoryPurgeCoordinator(session)
                        .RunConfirmedMoveAsync(
                            request,
                            confirmedPreview,
                            DateOnly.FromDateTime(DateTime.Now),
                            token)
                        .ConfigureAwait(false);
                }
                catch (ApplicationHistoryPurgePreviewOutdatedException)
                {
                    refusal = ApplicationGroupMoveOutcomeKind.PreviewOutdated;
                    throw;
                }
                catch (ApplicationGroupNameTakenException)
                {
                    refusal = ApplicationGroupMoveOutcomeKind.NameTaken;
                    throw;
                }
                catch (ApplicationHistoryPurgeIncompleteException exception)
                {
                    incomplete = exception;
                    throw;
                }
            },
            cancellationToken);

        if (result is not null)
        {
            return new ApplicationGroupMoveOutcome(ApplicationGroupMoveOutcomeKind.Completed, result);
        }
        if (incomplete is not null)
        {
            return new ApplicationGroupMoveOutcome(
                ApplicationGroupMoveOutcomeKind.Incomplete,
                IncompleteGroupId: incomplete.GroupId,
                IncompleteCurrentSummary: incomplete.CurrentSummary);
        }
        return new ApplicationGroupMoveOutcome(refusal ?? ApplicationGroupMoveOutcomeKind.Failed);
    }

    /// <summary>
    /// Runs one policy change with clipboard capture quiesced, after any in-flight recovery for the
    /// same session. Capture resumes on success, and after a failure only when no durable
    /// maintenance marker is left behind.
    /// </summary>
    private async Task<bool> TryRunPolicyChangeWithQuiescedRuntimeAsync(
        ProtectedStorageSessionLease session,
        Func<CancellationToken, Task> change,
        CancellationToken cancellationToken)
    {
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
            await Task.Run(() => change(token), token);

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

internal enum ApplicationGroupMoveOutcomeKind
{
    Completed,
    Incomplete,
    PreviewOutdated,
    NameTaken,
    Failed,
}

/// <summary>How a confirmed move ended, for the confirmation dialog.</summary>
internal sealed record ApplicationGroupMoveOutcome(
    ApplicationGroupMoveOutcomeKind Kind,
    ApplicationHistoryPurgeResult? Result = null,
    global::Clipensk.Core.Applications.ApplicationGroupId? IncompleteGroupId = null,
    global::Clipensk.Storage.History.ClipboardHistoryPurgeSummary? IncompleteCurrentSummary = null);
