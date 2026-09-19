using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Databases;

public enum ArchiveRotationPublicationCheckpoint
{
    BeforeTemporaryCopy = 0,
    AfterTemporaryCopy = 1,
    AfterTemporaryValidation = 2,
    BeforeFinalMove = 3,
    AfterFinalMove = 4,
    AfterFinalValidation = 5,
    BeforePhysicalPhaseCommit = 6,
    AfterPhysicalPhaseCommit = 7,
}

/// <summary>
/// Copy-first physical publication of a pending Archive Rotation, per
/// <c>docs/ARCHIVE_ROTATION_PROTOCOL.md</c> §9.
///
/// Every planned target is copied from its validated staging shadow to an operation-specific
/// temporary file, validated there, then atomically moved onto its canonical name without
/// overwrite and revalidated. The staging shadow is deliberately kept: it stays the retained backup
/// until the operation completes, because Current rows are only purged in a later phase.
///
/// Publication is roll-forward and idempotent. A planned final file that already exists is reused
/// after it validates against the immutable plan, so a crash between per-target moves is resumed
/// rather than restarted. Existing canonical files are never overwritten and a reserved name
/// occupied by anything unexpected is a fail-closed collision.
/// </summary>
public sealed class ProtectedArchiveRotationPublisher
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly SqlitePendingArchiveRotationRepository _pendingRotationRepository;
    private readonly ProtectedArchiveRotationShadowBuilder _shadowBuilder;
    private readonly Action<ArchiveRotationPublicationCheckpoint, int?>? _faultInjector;
    private readonly string _archiveDirectory;
    private readonly string _currentDatabasePath;

    public ProtectedArchiveRotationPublisher(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null,
        Action<ArchiveRotationPublicationCheckpoint, int?>? faultInjector = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
        _pendingRotationRepository = new SqlitePendingArchiveRotationRepository(
            session,
            _connectionFactory);
        _shadowBuilder = new ProtectedArchiveRotationShadowBuilder(session, _connectionFactory);
        _faultInjector = faultInjector;
        string dataRootPath = Path.GetFullPath(session.DataRootPath);
        _archiveDirectory = Path.Combine(dataRootPath, "Archive");
        _currentDatabasePath = Path.Combine(dataRootPath, "Current", "current.db");
    }

    public async Task<PendingArchiveRotationOperation> PublishOrRecoverAsync(
        Guid operationId,
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
            () => PublishOrRecoverCore(operationId, mutationLease, token),
            CancellationToken.None).ConfigureAwait(false);
    }

    internal string GetTemporaryPath(Guid operationId, ArchiveFileName fileName) =>
        Path.Combine(
            _archiveDirectory,
            $".clipensk-archive-rotation-{operationId:D}-{fileName.FileName}.tmp");

    private PendingArchiveRotationOperation PublishOrRecoverCore(
        Guid operationId,
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

        if (operation.Phase is ArchiveRotationPhase.PhysicalPublished
            or ArchiveRotationPhase.SourcePurged
            or ArchiveRotationPhase.CatalogPublished)
        {
            ValidatePublishedTargets(operation, token);
            ValidatePhysicalSet(token);
            return operation;
        }

        if (operation.Phase != ArchiveRotationPhase.ReadyToPublish)
        {
            throw new InvalidOperationException(
                "Archive rotation physical publication requires ReadyToPublish phase.");
        }

        bool anyAlreadyPublished = operation.Targets
            .Any(target => File.Exists(GetFinalPath(target.FileName)));
        if (!anyAlreadyPublished)
        {
            // A first attempt must prove the staged set still matches Current exactly. On a resumed
            // attempt Current may legitimately no longer be reachable for comparison of published
            // ranges, so the immutable plan and the final files carry the check instead.
            ValidateShadowSetAgainstPlan(operation, mutationLease, token);
        }

        foreach (PendingArchiveRotationTarget target in operation.Targets)
        {
            token.ThrowIfCancellationRequested();
            PublishTarget(operation.OperationId, target, token);
        }

        ValidatePhysicalSet(token);
        Hit(ArchiveRotationPublicationCheckpoint.BeforePhysicalPhaseCommit, null);
        token.ThrowIfCancellationRequested();

        PendingArchiveRotationOperation updated = AdvanceToPhysicalPublished(operation, token);

        // Deliberately not observing caller cancellation after the durable phase commit: a late
        // cancellation must not be reported as if the published state rolled back.
        Hit(ArchiveRotationPublicationCheckpoint.AfterPhysicalPhaseCommit, null);
        return updated;
    }

    /// <summary>
    /// Advances the durable phase on a connection this publisher opens itself. The mutation lease is
    /// already held for the whole publication, so the repository's lease-acquiring entry point must
    /// not be used here: re-entering it would deadlock against the lease we own.
    /// </summary>
    private PendingArchiveRotationOperation AdvanceToPhysicalPublished(
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
                ArchiveRotationPhase.ReadyToPublish,
                ArchiveRotationPhase.PhysicalPublished,
                token);

        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return updated;
    }

    private void PublishTarget(
        Guid operationId,
        PendingArchiveRotationTarget target,
        CancellationToken token)
    {
        string finalPath = GetFinalPath(target.FileName);
        if (Directory.Exists(finalPath))
        {
            throw new InvalidOperationException(
                $"Reserved Archive filename '{target.FileName.FileName}' is occupied by a directory.");
        }

        if (File.Exists(finalPath))
        {
            ValidateFinalTarget(target, token);
            return;
        }

        string stagedPath = _shadowBuilder.GetStagedArchivePath(operationId, target.FileName);
        if (!File.Exists(stagedPath) || Directory.Exists(stagedPath))
        {
            throw new FileNotFoundException(
                $"Staged Archive rotation shadow '{target.FileName.FileName}' was not found.",
                stagedPath);
        }

        ValidateArchiveAtPath(stagedPath, target, token);

        string temporaryPath = GetTemporaryPath(operationId, target.FileName);
        if (Directory.Exists(temporaryPath))
        {
            throw new InvalidOperationException(
                "Archive rotation temporary path is occupied by a directory.");
        }

        // A temporary file left by a crashed attempt is disposable and never authoritative.
        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }

        Hit(ArchiveRotationPublicationCheckpoint.BeforeTemporaryCopy, target.SegmentOrder);
        token.ThrowIfCancellationRequested();
        File.Copy(stagedPath, temporaryPath);
        Hit(ArchiveRotationPublicationCheckpoint.AfterTemporaryCopy, target.SegmentOrder);

        ValidateArchiveAtPath(temporaryPath, target, token);
        Hit(ArchiveRotationPublicationCheckpoint.AfterTemporaryValidation, target.SegmentOrder);

        Hit(ArchiveRotationPublicationCheckpoint.BeforeFinalMove, target.SegmentOrder);
        token.ThrowIfCancellationRequested();

        // File.Move without overwrite fails when the destination appeared meanwhile, which keeps an
        // unexpected occupant of a reserved canonical name fail-closed.
        File.Move(temporaryPath, finalPath);
        Hit(ArchiveRotationPublicationCheckpoint.AfterFinalMove, target.SegmentOrder);

        ValidateFinalTarget(target, token);
        Hit(ArchiveRotationPublicationCheckpoint.AfterFinalValidation, target.SegmentOrder);
    }

    private void ValidateShadowSetAgainstPlan(
        PendingArchiveRotationOperation operation,
        ProtectedStorageMutationLease mutationLease,
        CancellationToken token)
    {
        var plan = new ArchiveRotationShadowPlan(
            operation.OperationId,
            operation.PolicySnapshot,
            operation.Targets,
            OpenTail: null);
        _shadowBuilder.ValidateShadowSet(plan, mutationLease, token);
    }

    private void ValidatePublishedTargets(
        PendingArchiveRotationOperation operation,
        CancellationToken token)
    {
        foreach (PendingArchiveRotationTarget target in operation.Targets)
        {
            token.ThrowIfCancellationRequested();
            ValidateFinalTarget(target, token);
        }
    }

    private void ValidateFinalTarget(PendingArchiveRotationTarget target, CancellationToken token)
    {
        string finalPath = GetFinalPath(target.FileName);
        if (!File.Exists(finalPath))
        {
            throw new FileNotFoundException(
                $"Published Archive rotation target '{target.FileName.FileName}' was not found.",
                finalPath);
        }

        ValidateArchiveAtPath(finalPath, target, token);
    }

    /// <summary>
    /// Validates one physical file against the immutable planned target, including the physical size
    /// recorded when its shadow was measured. A published copy is byte-identical to that shadow, so a
    /// differing length means the file is not the planned evidence.
    /// </summary>
    private void ValidateArchiveAtPath(
        string databasePath,
        PendingArchiveRotationTarget target,
        CancellationToken token)
    {
        using (SqliteConnection connection = OpenDatabase(databasePath, token))
        {
            _ = ArchiveShadowWriter.ValidateShadowDatabase(
                connection,
                _session.StorageId,
                target.FileName,
                target.DatabaseId,
                target.Coverage,
                token);
        }

        long length = new FileInfo(databasePath).Length;
        if (length != target.ShadowPhysicalSizeBytes)
        {
            throw new InvalidDataException(
                $"Archive rotation target '{target.FileName.FileName}' does not match the planned physical size.");
        }
    }

    /// <summary>
    /// Revalidates the whole canonical Archive set: every file canonical, every coverage assigned,
    /// and no two coverages overlapping.
    /// </summary>
    private void ValidatePhysicalSet(CancellationToken token)
    {
        if (!Directory.Exists(_archiveDirectory))
        {
            throw new DirectoryNotFoundException("Archive directory was not found.");
        }

        var archiveService = new ProtectedArchiveDatabaseService(_session, _connectionFactory);
        var coverages = new List<JournalDateRange>();
        foreach (string archivePath in Directory.EnumerateFiles(
                     _archiveDirectory,
                     "archive_*.db",
                     SearchOption.TopDirectoryOnly))
        {
            token.ThrowIfCancellationRequested();
            string persistedFileName = Path.GetFileName(archivePath);
            if (!ArchiveFileName.TryParse(persistedFileName, out ArchiveFileName parsedFileName) ||
                !string.Equals(persistedFileName, parsedFileName.FileName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Archive file '{persistedFileName}' does not use a canonical Clipensk name.");
            }

            DatabaseIdentity identity = archiveService
                .ValidateAsync(parsedFileName, token)
                .GetAwaiter()
                .GetResult();
            if (identity.CoverageStartDate is not DateOnly coverageStart ||
                identity.CoverageEndDate is not DateOnly coverageEnd)
            {
                throw new InvalidDataException(
                    $"Archive '{persistedFileName}' does not have assigned coverage.");
            }

            coverages.Add(new JournalDateRange(coverageStart, coverageEnd));
        }

        coverages.Sort(static (left, right) => left.StartDate.CompareTo(right.StartDate));
        for (int index = 1; index < coverages.Count; index++)
        {
            if (coverages[index].Intersects(coverages[index - 1]))
            {
                throw new InvalidDataException(
                    "Physical Archive coverage overlaps after archive rotation publication.");
            }
        }
    }

    private string GetFinalPath(ArchiveFileName fileName)
    {
        if (!ArchiveShadowWriter.IsCanonicalArchiveFileName(fileName) ||
            fileName.SplitSequence != ArchiveFileName.NoSplit)
        {
            throw new InvalidDataException(
                "Archive rotation plan contains a noncanonical or split filename.");
        }

        return Path.Combine(_archiveDirectory, fileName.FileName);
    }

    private SqliteConnection OpenDatabase(string databasePath, CancellationToken token)
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

    private void Hit(ArchiveRotationPublicationCheckpoint checkpoint, int? segmentOrder) =>
        _faultInjector?.Invoke(checkpoint, segmentOrder);
}
