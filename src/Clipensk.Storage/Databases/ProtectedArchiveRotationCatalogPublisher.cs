using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Databases;

public enum ArchiveRotationCatalogCheckpoint
{
    BeforeCatalogReplacement = 0,
    AfterCatalogReplacement = 1,
    AfterCatalogValidation = 2,
    BeforeCatalogPhaseCommit = 3,
    AfterCatalogPhaseCommit = 4,
    AfterStagingDelete = 5,
    BeforeMarkerClear = 6,
}

/// <summary>
/// Catalog publication and final cleanup for a pending Archive Rotation, per
/// <c>docs/ARCHIVE_ROTATION_PROTOCOL.md</c> §11.
///
/// Catalog is a rebuildable projection, never the operation journal, so this rebuilds it from the
/// authoritative Archive databases rather than writing rotation bookkeeping into it. Rotation does
/// not change the logical set of external payload addresses — references only moved from Current to
/// Archive — so no separate external-payload projection mutation is required here.
///
/// The retained staging shadows are deleted only after Catalog validation succeeds and the durable
/// phase has advanced, because until then they are the backup that proves the published targets.
/// </summary>
public sealed class ProtectedArchiveRotationCatalogPublisher
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly SqlitePendingArchiveRotationRepository _pendingRotationRepository;
    private readonly ProtectedArchiveRotationShadowBuilder _shadowBuilder;
    private readonly ProtectedArchiveSegmentCatalog _archiveCatalog;
    private readonly ProtectedStorageCatalogReplacementService _catalogReplacement;
    private readonly Action<ArchiveRotationCatalogCheckpoint>? _faultInjector;
    private readonly string _dataRootPath;
    private readonly string _archiveDirectory;
    private readonly string _currentDatabasePath;

    public ProtectedArchiveRotationCatalogPublisher(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null,
        Action<ArchiveRotationCatalogCheckpoint>? faultInjector = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
        _pendingRotationRepository = new SqlitePendingArchiveRotationRepository(
            session,
            _connectionFactory);
        _shadowBuilder = new ProtectedArchiveRotationShadowBuilder(session, _connectionFactory);
        _archiveCatalog = new ProtectedArchiveSegmentCatalog(session, _connectionFactory);
        _catalogReplacement = new ProtectedStorageCatalogReplacementService(_connectionFactory);
        _faultInjector = faultInjector;
        _dataRootPath = Path.GetFullPath(session.DataRootPath);
        _archiveDirectory = Path.Combine(_dataRootPath, "Archive");
        _currentDatabasePath = Path.Combine(_dataRootPath, "Current", "current.db");
    }

    public async Task<IReadOnlyList<ArchiveSegmentDescriptor>> PublishOrRecoverAsync(
        Guid operationId,
        DateOnly currentCalendarDate,
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

        PendingArchiveRotationOperation operation =
            await _pendingRotationRepository.ReadAsync(token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No pending archive rotation exists.");
        if (operation.OperationId != operationId)
        {
            throw new InvalidOperationException(
                "Pending archive rotation ownership does not match the requested operation.");
        }
        if (operation.Phase is not (ArchiveRotationPhase.SourcePurged
            or ArchiveRotationPhase.CatalogPublished))
        {
            throw new InvalidOperationException(
                "Archive rotation Catalog publication requires SourcePurged or CatalogPublished phase.");
        }

        ValidatePlannedTargets(operation, token);

        IReadOnlyList<ArchiveSegmentDescriptor> descriptors;
        if (operation.Phase == ArchiveRotationPhase.SourcePurged)
        {
            ValidateRetainedShadows(operation);
            Hit(ArchiveRotationCatalogCheckpoint.BeforeCatalogReplacement);
            token.ThrowIfCancellationRequested();

            ProtectedStorageCatalogReplacementResult replacement =
                await _catalogReplacement.ReplaceExistingCatalogAsync(
                    _dataRootPath,
                    _session.StorageId,
                    _session.DangerousGetMasterKeyMemory(),
                    currentCalendarDate,
                    token).ConfigureAwait(false);
            if (!replacement.IsSuccess)
            {
                throw new InvalidDataException(
                    $"Archive rotation Catalog replacement failed with status {replacement.Status}.");
            }

            Hit(ArchiveRotationCatalogCheckpoint.AfterCatalogReplacement);
            descriptors = await ValidateCatalogAsync(operation, currentCalendarDate, token)
                .ConfigureAwait(false);
            ValidateRetainedShadows(operation);
            Hit(ArchiveRotationCatalogCheckpoint.AfterCatalogValidation);
            Hit(ArchiveRotationCatalogCheckpoint.BeforeCatalogPhaseCommit);
            token.ThrowIfCancellationRequested();

            operation = AdvanceToCatalogPublished(operation, token);

            // After this durable phase commit a caller cancellation is not a rollback: recovery can
            // always finish cleanup from CatalogPublished.
            Hit(ArchiveRotationCatalogCheckpoint.AfterCatalogPhaseCommit);
        }
        else
        {
            descriptors = await ValidateCatalogAsync(operation, currentCalendarDate, token)
                .ConfigureAwait(false);
        }

        CancellationToken cleanupToken = _session.CancellationToken;
        ValidatePlannedTargets(operation, cleanupToken);
        DeleteStagingIfPresent(operation.OperationId);
        Hit(ArchiveRotationCatalogCheckpoint.AfterStagingDelete);
        Hit(ArchiveRotationCatalogCheckpoint.BeforeMarkerClear);
        ClearCompleted(operation.OperationId);
        return descriptors;
    }

    private async Task<IReadOnlyList<ArchiveSegmentDescriptor>> ValidateCatalogAsync(
        PendingArchiveRotationOperation operation,
        DateOnly currentCalendarDate,
        CancellationToken token)
    {
        IReadOnlyList<ArchiveSegmentDescriptor> descriptors =
            await _archiveCatalog.ValidateConsistencyAsync(currentCalendarDate, token)
                .ConfigureAwait(false);
        ValidatePlannedDescriptors(operation, descriptors);
        return descriptors;
    }

    private void ValidatePlannedTargets(
        PendingArchiveRotationOperation operation,
        CancellationToken token)
    {
        var archiveService = new ProtectedArchiveDatabaseService(_session, _connectionFactory);
        foreach (PendingArchiveRotationTarget target in operation.Targets)
        {
            token.ThrowIfCancellationRequested();
            DatabaseIdentity identity = archiveService
                .ValidateAsync(target.FileName, token)
                .GetAwaiter()
                .GetResult();
            if (identity.DatabaseId != target.DatabaseId ||
                identity.CoverageStartDate != target.Coverage.StartDate ||
                identity.CoverageEndDate != target.Coverage.EndDate)
            {
                throw new InvalidDataException(
                    $"Published Archive rotation target '{target.FileName.FileName}' "
                        + "does not match the planned identity or coverage.");
            }
        }
    }

    private static void ValidatePlannedDescriptors(
        PendingArchiveRotationOperation operation,
        IReadOnlyList<ArchiveSegmentDescriptor> descriptors)
    {
        foreach (PendingArchiveRotationTarget target in operation.Targets)
        {
            ArchiveSegmentDescriptor? descriptor = descriptors.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.FileName,
                    target.FileName.FileName,
                    StringComparison.Ordinal));
            if (descriptor is null ||
                descriptor.DatabaseId != target.DatabaseId ||
                descriptor.Coverage != target.Coverage)
            {
                throw new InvalidDataException(
                    $"Catalog projection does not describe rotation target "
                        + $"'{target.FileName.FileName}' as planned.");
            }
        }
    }

    /// <summary>
    /// Until Catalog publication completes, the staging shadows are the retained backup for history
    /// that no longer exists in Current, so losing them before that point is fail-closed.
    /// </summary>
    private void ValidateRetainedShadows(PendingArchiveRotationOperation operation)
    {
        foreach (PendingArchiveRotationTarget target in operation.Targets)
        {
            string stagedPath = _shadowBuilder.GetStagedArchivePath(
                operation.OperationId,
                target.FileName);
            if (!File.Exists(stagedPath))
            {
                throw new FileNotFoundException(
                    $"Retained Archive rotation shadow '{target.FileName.FileName}' was not found.",
                    stagedPath);
            }
        }
    }

    private void DeleteStagingIfPresent(Guid operationId)
    {
        string stagingDirectory = _shadowBuilder.GetStagingDirectory(operationId);
        if (Directory.Exists(stagingDirectory))
        {
            Directory.Delete(stagingDirectory, recursive: true);
        }
    }

    private PendingArchiveRotationOperation AdvanceToCatalogPublished(
        PendingArchiveRotationOperation operation,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using SqliteConnection connection = OpenCurrentForWrite();
        using SqliteTransaction transaction = connection.BeginTransaction();
        PendingArchiveRotationOperation updated =
            SqlitePendingArchiveRotationRepository.AdvancePhaseInTransaction(
                connection,
                transaction,
                operation.OperationId,
                ArchiveRotationPhase.SourcePurged,
                ArchiveRotationPhase.CatalogPublished,
                token);

        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return updated;
    }

    private void ClearCompleted(Guid operationId)
    {
        using SqliteConnection connection = OpenCurrentForWrite();
        using SqliteTransaction transaction = connection.BeginTransaction();
        SqlitePendingArchiveRotationRepository.ClearCompletedInTransaction(
            connection,
            transaction,
            operationId,
            CancellationToken.None);
        transaction.Commit();
    }

    /// <summary>
    /// Opens Current directly because the mutation lease is already held for the whole publication;
    /// the repository's lease-acquiring entry points would deadlock against it.
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

    private void Hit(ArchiveRotationCatalogCheckpoint checkpoint) => _faultInjector?.Invoke(checkpoint);
}
