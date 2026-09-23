using Clipensk.Core.Applications;
using Clipensk.Core.Clipboard;
using Clipensk.Core.Storage;
using Clipensk.Storage.History;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Clipboard;

public sealed record ApplicationHistoryPurgeStartResult(
    Guid OperationId,
    ClipboardHistoryPurgeSummary CurrentSummary);

internal enum ApplicationHistoryPurgeStartCheckpoint
{
    BeforeCommit,
    AfterCommit,
}

/// <summary>
/// Starts an <c>ApplicationHistoryPurge</c> operation, per
/// <c>docs/APPLICATION_GROUP_PROTOCOL.md</c> §5: one Current transaction publishes the root's policy,
/// purges Current by the fixed rule and records the durable marker. The Archive, Catalog and Trash
/// phases continue in <see cref="ProtectedApplicationHistoryPurgeContinuation"/>.
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

    /// <summary>
    /// Gives an unconfigured group root its first personal policy and purges, across the root's
    /// whole group, the saved representations that policy disallows.
    /// </summary>
    public async Task<ApplicationHistoryPurgeStartResult> StartFirstAssignmentAsync(
        ApplicationId rootApplicationId,
        ClipboardCapturePolicy policy,
        IReadOnlyList<ApplicationCustomBinaryFormatConfiguration>? customBinaryConfigurations = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rootApplicationId);
        CapturePolicySql.ValidateApplicationPolicy(policy);
        Dictionary<string, string> requestedMappings = CapturePolicySql.NormalizeCustomBinaryConfigurations(
            policy,
            customBinaryConfigurations ?? []);

        return await RunAsync(
                (connection, transaction, token) =>
                {
                    ApplicationHistoryPurgeScope scope =
                        ApplicationHistoryPurgeScope.ResolveFirstAssignmentInTransaction(
                            connection,
                            transaction,
                            rootApplicationId,
                            policy,
                            token);

                    CapturePolicySql.InsertMissingCustomBinaryConfigurationsInTransaction(
                        connection,
                        transaction,
                        requestedMappings,
                        token);
                    CapturePolicySql.WriteApplicationPolicyInTransaction(
                        connection,
                        transaction,
                        rootApplicationId,
                        policy,
                        replaceExisting: false,
                        token);

                    return ApplicationHistoryPurgeStateCodec.CreateStarted(
                        ApplicationHistoryPurgeReason.FirstAssignment,
                        rootApplicationId.ToString(),
                        childApplicationId: null,
                        scope.SourceApplicationIds.Select(static id => id.ToString()),
                        scope.Rule,
                        ApplicationPolicyMaintenanceStateCodec.ComputePolicyFingerprint(policy));
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ApplicationHistoryPurgeStartResult> RunAsync(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, ApplicationHistoryPurgeState> prepare,
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

                    ApplicationHistoryPurgeState state = prepare(connection, transaction, token);
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
                    return new ApplicationHistoryPurgeStartResult(operation.OperationId, plan.Summary);
                },
                CancellationToken.None)
            .ConfigureAwait(false);
    }
}
