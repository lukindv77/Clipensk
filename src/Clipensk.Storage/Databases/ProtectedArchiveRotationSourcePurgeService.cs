using System.Globalization;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Databases;

public sealed record ArchiveRotationSourcePurgeResult(
    PendingArchiveRotationOperation Operation,
    int PurgedEventCount,
    int AlreadyPurgedRangeCount);

/// <summary>
/// Verified Current purge for a published Archive Rotation, per
/// <c>docs/ARCHIVE_ROTATION_PROTOCOL.md</c> §10.
///
/// This does not implement its own transfer. It reuses
/// <see cref="ProtectedCurrentToArchiveTransferService"/>'s exact copy-verify-compare-purge core
/// through its lease-aware entry point, so rotation and manual transfer cannot drift apart and the
/// mutation lease is acquired exactly once for the whole operation.
///
/// Every range is accepted only against the immutable planned target and the retained staging
/// shadow, which keeps a range that a previous attempt already purged verifiable after the fact.
/// Rows belonging to the open tail are never touched: they are outside every planned coverage.
/// </summary>
public sealed class ProtectedArchiveRotationSourcePurgeService
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly SqlitePendingArchiveRotationRepository _pendingRotationRepository;
    private readonly ProtectedArchiveRotationShadowBuilder _shadowBuilder;
    private readonly ProtectedCurrentToArchiveTransferService _transferService;
    private readonly string _archiveDirectory;
    private readonly string _currentDatabasePath;

    public ProtectedArchiveRotationSourcePurgeService(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
        _pendingRotationRepository = new SqlitePendingArchiveRotationRepository(
            session,
            _connectionFactory);
        _shadowBuilder = new ProtectedArchiveRotationShadowBuilder(session, _connectionFactory);
        _transferService = new ProtectedCurrentToArchiveTransferService(session, _connectionFactory);
        string dataRootPath = Path.GetFullPath(session.DataRootPath);
        _archiveDirectory = Path.Combine(dataRootPath, "Archive");
        _currentDatabasePath = Path.Combine(dataRootPath, "Current", "current.db");
    }

    public async Task<ArchiveRotationSourcePurgeResult> PurgeOrRecoverAsync(
        Guid operationId,
        DateOnly currentLocalDate,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException(
                "Archive rotation operation id cannot be empty.",
                nameof(operationId));
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            cancellationToken);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();

        using ProtectedStorageMutationLease mutationLease =
            await _session.AcquireMutationLeaseAsync(token).ConfigureAwait(false);

        return await Task.Run(
            () => PurgeOrRecoverCore(operationId, currentLocalDate, mutationLease, token),
            CancellationToken.None).ConfigureAwait(false);
    }

    private ArchiveRotationSourcePurgeResult PurgeOrRecoverCore(
        Guid operationId,
        DateOnly currentLocalDate,
        ProtectedStorageMutationLease mutationLease,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        PendingArchiveRotationOperation operation =
            _pendingRotationRepository.ReadAsync(token).AsTask().GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("No pending archive rotation exists.");

        if (operation.OperationId != operationId)
        {
            throw new InvalidOperationException(
                "Pending archive rotation ownership does not match the requested operation.");
        }

        if (operation.Phase is ArchiveRotationPhase.SourcePurged
            or ArchiveRotationPhase.CatalogPublished)
        {
            VerifyAllRangesComplete(operation, token);
            return new ArchiveRotationSourcePurgeResult(operation, 0, operation.Targets.Count);
        }

        if (operation.Phase != ArchiveRotationPhase.PhysicalPublished)
        {
            throw new InvalidOperationException(
                "Archive rotation source purge requires PhysicalPublished phase.");
        }

        int purgedEventCount = 0;
        int alreadyPurgedRangeCount = 0;
        foreach (PendingArchiveRotationTarget target in operation.Targets)
        {
            token.ThrowIfCancellationRequested();
            VerifyPublishedTargetAgainstShadow(operation.OperationId, target, token);

            if (CountCurrentRows(target.Coverage, token) == 0)
            {
                // A previous attempt already completed this range. The published target and its
                // retained shadow, verified just above, are what make that acceptable.
                alreadyPurgedRangeCount++;
                continue;
            }

            CurrentToArchiveTransferResult transferred = _transferService.TransferUnderMutationLease(
                target.FileName,
                target.Coverage,
                currentLocalDate,
                mutationLease,
                token);
            purgedEventCount += transferred.PurgedEventCount;
        }

        VerifyAllRangesComplete(operation, token);
        token.ThrowIfCancellationRequested();

        PendingArchiveRotationOperation updated = AdvanceToSourcePurged(operation, token);

        // Deliberately not observing caller cancellation after the durable phase commit.
        return new ArchiveRotationSourcePurgeResult(
            updated,
            purgedEventCount,
            alreadyPurgedRangeCount);
    }

    private void VerifyAllRangesComplete(
        PendingArchiveRotationOperation operation,
        CancellationToken token)
    {
        foreach (PendingArchiveRotationTarget target in operation.Targets)
        {
            token.ThrowIfCancellationRequested();
            VerifyPublishedTargetAgainstShadow(operation.OperationId, target, token);

            long remaining = CountCurrentRows(target.Coverage, token);
            if (remaining != 0)
            {
                throw new InvalidDataException(
                    $"Current still holds {remaining} rows inside published rotation coverage "
                        + $"'{target.FileName.FileName}'.");
            }
        }
    }

    /// <summary>
    /// Verifies the published Archive against the immutable plan and against the retained staging
    /// shadow. Once Current rows are gone the shadow is the only remaining independent copy, so it
    /// is what proves the published file is the planned history.
    /// </summary>
    private void VerifyPublishedTargetAgainstShadow(
        Guid operationId,
        PendingArchiveRotationTarget target,
        CancellationToken token)
    {
        string finalPath = Path.Combine(_archiveDirectory, target.FileName.FileName);
        if (!File.Exists(finalPath))
        {
            throw new FileNotFoundException(
                $"Published Archive rotation target '{target.FileName.FileName}' was not found.",
                finalPath);
        }

        string stagedPath = _shadowBuilder.GetStagedArchivePath(operationId, target.FileName);
        if (!File.Exists(stagedPath))
        {
            throw new FileNotFoundException(
                $"Retained Archive rotation shadow '{target.FileName.FileName}' was not found; "
                    + "the published target can no longer be proven against the plan.",
                stagedPath);
        }

        using SqliteConnection final = OpenReadOnly(finalPath, token);
        _ = ArchiveShadowWriter.ValidateShadowDatabase(
            final,
            _session.StorageId,
            target.FileName,
            target.DatabaseId,
            target.Coverage,
            token);

        using SqliteConnection shadow = OpenReadOnly(stagedPath, token);
        ArchiveShadowWriter.CompareCoverage(
            shadow,
            final,
            target.Coverage,
            "Archive rotation",
            token);
    }

    private long CountCurrentRows(JournalDateRange coverage, CancellationToken token)
    {
        using SqliteConnection connection = OpenReadOnly(_currentDatabasePath, token);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM ClipboardHistoryEvent
            WHERE CalendarDate >= $start AND CalendarDate <= $end;
            """;
        command.Parameters.AddWithValue("$start", FormatDate(coverage.StartDate));
        command.Parameters.AddWithValue("$end", FormatDate(coverage.EndDate));
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Advances the durable phase on a connection this service opens itself, because the mutation
    /// lease is already held for the whole purge.
    /// </summary>
    private PendingArchiveRotationOperation AdvanceToSourcePurged(
        PendingArchiveRotationOperation operation,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using SqliteConnection connection = _connectionFactory.Open(
            _currentDatabasePath,
            _session.DangerousGetMasterKeyMemory(),
            SqliteOpenMode.ReadWrite);
        using (SqliteCommand foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_keys = ON;";
            foreignKeys.ExecuteNonQuery();
        }

        using SqliteTransaction transaction = connection.BeginTransaction();
        PendingArchiveRotationOperation updated =
            SqlitePendingArchiveRotationRepository.AdvancePhaseInTransaction(
                connection,
                transaction,
                operation.OperationId,
                ArchiveRotationPhase.PhysicalPublished,
                ArchiveRotationPhase.SourcePurged,
                token);

        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return updated;
    }

    private SqliteConnection OpenReadOnly(string databasePath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_session.IsActive)
        {
            throw new OperationCanceledException(_session.CancellationToken);
        }

        SqliteConnection connection = _connectionFactory.Open(
            databasePath,
            _session.DangerousGetMasterKeyMemory(),
            SqliteOpenMode.ReadOnly);
        try
        {
            using (SqliteCommand foreignKeys = connection.CreateCommand())
            {
                foreignKeys.CommandText = "PRAGMA foreign_keys = ON;";
                foreignKeys.ExecuteNonQuery();
            }

            token.ThrowIfCancellationRequested();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static string FormatDate(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
