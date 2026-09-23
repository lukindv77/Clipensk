using Clipensk.Core.Applications;
using Clipensk.Core.Clipboard;
using Clipensk.Core.Storage;
using Clipensk.Storage.Applications;
using Clipensk.Storage.History;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Clipboard;

public sealed record ApplicationHistoryPurgeStartResult(
    Guid OperationId,
    ApplicationGroupId GroupId,
    ClipboardHistoryPurgeSummary CurrentSummary);

/// <summary>
/// The move no longer does what the preview the user confirmed described — the target group's
/// policy, the groups or the application's membership changed in between. Nothing was written;
/// preview again.
/// </summary>
public sealed class ApplicationHistoryPurgePreviewOutdatedException : InvalidOperationException
{
    public ApplicationHistoryPurgePreviewOutdatedException()
        : base("The confirmed history purge preview is outdated; preview again before purging.")
    {
    }
}

internal enum ApplicationHistoryPurgeStartCheckpoint
{
    BeforeCommit,
    AfterCommit,
}

/// <summary>
/// Starts an <c>ApplicationHistoryPurge</c> operation — moving an application into a user group —
/// per <c>docs/APPLICATION_GROUP_PROTOCOL.md</c> §5: one Current transaction creates the target
/// group when it is new, moves the application, optionally deletes its emptied source group, purges
/// the application's Current history by the target group's rule and records the durable marker. The
/// Archive, Catalog and Trash phases continue in <see cref="ProtectedApplicationHistoryPurgeContinuation"/>.
/// </summary>
public sealed class ProtectedApplicationHistoryPurgeService
{
    public const string OperationKind = ApplicationHistoryPurgeState.OperationKind;

    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly Action<ApplicationHistoryPurgeStartCheckpoint>? _checkpoint;

    public ProtectedApplicationHistoryPurgeService(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
        : this(session, connectionFactory, checkpoint: null)
    {
    }

    internal ProtectedApplicationHistoryPurgeService(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory,
        Action<ApplicationHistoryPurgeStartCheckpoint>? checkpoint)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
        _checkpoint = checkpoint;
    }

    /// <summary>Moves the application and purges what the target group's rules disallow.</summary>
    public Task<ApplicationHistoryPurgeStartResult> StartMoveAsync(
        ApplicationGroupMoveRequest request,
        CancellationToken cancellationToken = default) =>
        StartMoveCoreAsync(request, confirmedPreview: null, cancellationToken);

    /// <summary>
    /// Starts the move the user confirmed from <paramref name="confirmedPreview"/>. It proceeds only
    /// while the move still does exactly what the preview described; otherwise it throws
    /// <see cref="ApplicationHistoryPurgePreviewOutdatedException"/> without writing. Records
    /// captured after the preview fall under the same confirmed rule.
    /// </summary>
    public Task<ApplicationHistoryPurgeStartResult> StartConfirmedMoveAsync(
        ApplicationGroupMoveRequest request,
        ApplicationGroupMovePreview confirmedPreview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(confirmedPreview);
        return StartMoveCoreAsync(request, confirmedPreview, cancellationToken);
    }

    private Task<ApplicationHistoryPurgeStartResult> StartMoveCoreAsync(
        ApplicationGroupMoveRequest request,
        ApplicationGroupMovePreview? confirmedPreview,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Dictionary<string, string> requestedMappings = request.Target is NewApplicationGroupTarget created
            ? CapturePolicySql.NormalizeCustomBinaryConfigurations(
                created.Policy,
                created.CustomBinaryConfigurations ?? [])
            : new Dictionary<string, string>(StringComparer.Ordinal);

        return RunAsync(
            (connection, transaction, token) =>
            {
                ApplicationHistoryPurgeScope scope = ApplicationHistoryPurgeScope.ResolveMoveInTransaction(
                    connection,
                    transaction,
                    request,
                    token);
                if (confirmedPreview is not null && !scope.Matches(confirmedPreview))
                {
                    throw new ApplicationHistoryPurgePreviewOutdatedException();
                }

                DateTimeOffset now = DateTimeOffset.UtcNow;
                ApplicationGroupId groupId;
                if (scope.TargetGroupId is null)
                {
                    CapturePolicySql.InsertMissingCustomBinaryConfigurationsInTransaction(
                        connection,
                        transaction,
                        requestedMappings,
                        token);
                    var group = new ApplicationGroup(
                        ApplicationGroupId.New(),
                        scope.TargetGroupName,
                        scope.TargetPolicy,
                        now);
                    ApplicationGroupSql.InsertGroupInTransaction(connection, transaction, group, token);
                    groupId = group.GroupId;
                }
                else
                {
                    groupId = scope.TargetGroupId;
                }

                ApplicationGroupSql.SetMembershipInTransaction(
                    connection,
                    transaction,
                    scope.ApplicationId,
                    groupId,
                    now);
                if (request.DeleteEmptiedSourceGroup)
                {
                    ApplicationGroupSql.DeleteEmptyGroupInTransaction(connection, transaction, scope.SourceGroupId!);
                }

                // The fingerprint is taken from the policy as stored, exactly as every later phase
                // reads it back to verify the group did not change.
                ApplicationGroup stored = ApplicationGroupSql.ReadGroupInTransaction(connection, transaction, groupId, token)
                    ?? throw new InvalidOperationException("The target application group disappeared.");
                return (groupId, ApplicationHistoryPurgeStateCodec.CreateStarted(
                    scope.ApplicationId.ToString(),
                    groupId.ToString(),
                    createdGroup: scope.TargetGroupId is null,
                    scope.Rule,
                    ApplicationPolicyMaintenanceStateCodec.ComputePolicyFingerprint(stored.Policy)));
            },
            cancellationToken);
    }

    private async Task<ApplicationHistoryPurgeStartResult> RunAsync(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, (ApplicationGroupId GroupId, ApplicationHistoryPurgeState State)> prepare,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            cancellationToken);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();

        using ProtectedStorageMutationLease mutationLease =
            await _session.AcquireMutationLeaseAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        return await Task.Run(
                () =>
                {
                    using SqliteConnection connection = ApplicationGroupMaintenanceDatabase.OpenCurrent(
                        _session,
                        _connectionFactory,
                        SqliteOpenMode.ReadWrite,
                        token);
                    using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
                    if (SqlitePendingPolicyMaintenanceRepository.ReadInTransaction(
                            connection,
                            transaction,
                            token) is not null)
                    {
                        throw new PendingPolicyMaintenanceException();
                    }

                    (ApplicationGroupId groupId, ApplicationHistoryPurgeState state) =
                        prepare(connection, transaction, token);
                    ClipboardHistoryPurgePlan plan = ClipboardHistoryPurge.PlanInTransaction(
                        connection,
                        transaction,
                        state.SourceApplicationIds,
                        state.Rule,
                        token);
                    ClipboardHistoryPurge.ApplyInTransaction(connection, transaction, plan, token);

                    PendingPolicyMaintenanceOperation operation =
                        SqlitePendingPolicyMaintenanceRepository.StartInTransaction(
                            connection,
                            transaction,
                            OperationKind,
                            ApplicationHistoryPurgeStateCodec.Serialize(state),
                            DateTimeOffset.UtcNow,
                            token);

                    _checkpoint?.Invoke(ApplicationHistoryPurgeStartCheckpoint.BeforeCommit);

                    // Final cancellation boundary: after COMMIT the policy, the Current purge and the
                    // marker are one durable state and the start is reported as success.
                    token.ThrowIfCancellationRequested();
                    transaction.Commit();
                    _checkpoint?.Invoke(ApplicationHistoryPurgeStartCheckpoint.AfterCommit);
                    return new ApplicationHistoryPurgeStartResult(operation.OperationId, groupId, plan.Summary);
                },
                CancellationToken.None)
            .ConfigureAwait(false);
    }
}
