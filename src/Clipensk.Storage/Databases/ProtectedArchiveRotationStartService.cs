using Clipensk.Core.History;
using Clipensk.Core.Settings;
using Clipensk.Core.Storage;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Databases;

public sealed record ArchiveRotationStartResult(
    bool Started,
    Guid OperationId,
    IReadOnlyList<PendingArchiveRotationTarget> Targets,
    JournalDateRange? OpenTail);

/// <summary>
/// Atomic start of an Archive Rotation, per <c>docs/ARCHIVE_ROTATION_PROTOCOL.md</c> §7.
///
/// One mutation lease covers the whole critical interval: the authoritative Current snapshot,
/// threshold planning, shadow construction, the durable marker commit and the advance to
/// <see cref="ArchiveRotationPhase.ReadyToPublish"/>. Nothing in the canonical Archive set is
/// touched here — after this service returns, Current is still the durable source and every planned
/// output exists only as a validated staging shadow.
///
/// When no segment reaches the configured rule, rotation is a no-op: no marker is committed and the
/// staging directory is left empty.
/// </summary>
public sealed class ProtectedArchiveRotationStartService
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly ProtectedArchiveRotationShadowBuilder _shadowBuilder;
    private readonly string _currentDatabasePath;

    public ProtectedArchiveRotationStartService(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
        _shadowBuilder = new ProtectedArchiveRotationShadowBuilder(session, _connectionFactory);
        _currentDatabasePath = Path.Combine(
            Path.GetFullPath(session.DataRootPath),
            "Current",
            "current.db");
    }

    public async Task<ArchiveRotationStartResult> StartAsync(
        ArchiveRotationSettings settings,
        DateOnly currentLocalDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            cancellationToken);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();

        using ProtectedStorageMutationLease mutationLease =
            await _session.AcquireMutationLeaseAsync(token).ConfigureAwait(false);

        return await Task.Run(
            () => StartCore(settings, currentLocalDate, mutationLease, token),
            CancellationToken.None).ConfigureAwait(false);
    }

    private ArchiveRotationStartResult StartCore(
        ArchiveRotationSettings settings,
        DateOnly currentLocalDate,
        ProtectedStorageMutationLease mutationLease,
        CancellationToken token)
    {
        Guid operationId = Guid.NewGuid();

        // The builder re-runs the scanner under this same lease, so a pending rotation or pending
        // split still blocks the start before any staging work happens.
        ArchiveRotationShadowPlan plan = _shadowBuilder.Build(
            operationId,
            settings,
            currentLocalDate,
            mutationLease,
            token);

        if (plan.Targets.Count == 0)
        {
            // No ready range means no marker, per §7. The empty staging directory is disposable.
            DeleteStagingIfPresent(operationId);
            return new ArchiveRotationStartResult(
                Started: false,
                Guid.Empty,
                Array.Empty<PendingArchiveRotationTarget>(),
                plan.OpenTail);
        }

        token.ThrowIfCancellationRequested();
        try
        {
            CommitPlannedMarker(operationId, plan, token);
        }
        catch
        {
            // Until the marker exists nothing can ever find this staging directory again, so a
            // failed commit must not leave an orphan behind. Once the marker is committed the
            // staging shadows are recovery state and are never cleaned here.
            DeleteStagingIfPresent(operationId);
            throw;
        }

        // Revalidate the staged shadows against what is now durable, before anything may publish.
        _shadowBuilder.ValidateShadowSet(plan, mutationLease, token);

        token.ThrowIfCancellationRequested();
        AdvanceToReadyToPublish(operationId, token);

        return new ArchiveRotationStartResult(
            Started: true,
            operationId,
            plan.Targets,
            plan.OpenTail);
    }

    private void CommitPlannedMarker(
        Guid operationId,
        ArchiveRotationShadowPlan plan,
        CancellationToken token)
    {
        using SqliteConnection connection = OpenCurrentForWrite();
        using SqliteTransaction transaction = connection.BeginTransaction();
        _ = SqlitePendingArchiveRotationRepository.StartInTransaction(
            connection,
            transaction,
            plan.PolicySnapshot,
            plan.Targets,
            operationId,
            DateTimeOffset.UtcNow,
            token);
        token.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private void AdvanceToReadyToPublish(Guid operationId, CancellationToken token)
    {
        using SqliteConnection connection = OpenCurrentForWrite();
        using SqliteTransaction transaction = connection.BeginTransaction();
        _ = SqlitePendingArchiveRotationRepository.AdvancePhaseInTransaction(
            connection,
            transaction,
            operationId,
            ArchiveRotationPhase.Planned,
            ArchiveRotationPhase.ReadyToPublish,
            token);
        token.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private void DeleteStagingIfPresent(Guid operationId)
    {
        string stagingDirectory = _shadowBuilder.GetStagingDirectory(operationId);
        if (Directory.Exists(stagingDirectory))
        {
            Directory.Delete(stagingDirectory, recursive: true);
        }
    }

    /// <summary>
    /// Opens Current directly: the mutation lease is already held for the whole start, so the
    /// repository's lease-acquiring entry points would deadlock against it.
    /// </summary>
    private SqliteConnection OpenCurrentForWrite()
    {
        SqliteConnection connection = _connectionFactory.Open(
            _currentDatabasePath,
            _session.DangerousGetMasterKeyMemory(),
            SqliteOpenMode.ReadWrite);
        try
        {
            using SqliteCommand foreignKeys = connection.CreateCommand();
            foreignKeys.CommandText = "PRAGMA foreign_keys = ON;";
            foreignKeys.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
