using System.Globalization;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Applications;
using Clipensk.Storage.History;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Databases;

public sealed class ProtectedArchiveSplitShadowBuilder
{
    private const string ArchiveSplitOperationLabel = "Archive split";

    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly SqlitePendingArchiveSplitRepository _pendingSplitRepository;
    private readonly string _archiveDirectory;
    private readonly string _currentDatabasePath;

    public ProtectedArchiveSplitShadowBuilder(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
        _pendingSplitRepository = new SqlitePendingArchiveSplitRepository(
            session,
            _connectionFactory);
        string dataRootPath = Path.GetFullPath(session.DataRootPath);
        _archiveDirectory = Path.Combine(dataRootPath, "Archive");
        _currentDatabasePath = Path.Combine(dataRootPath, "Current", "current.db");
    }

    public async Task<PendingArchiveSplitOperation> BuildAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException(
                "Archive split operation id cannot be empty.",
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
            () => BuildCore(operationId, token),
            CancellationToken.None).ConfigureAwait(false);
    }

    internal void ValidateShadowSet(
        PendingArchiveSplitOperation operation,
        ProtectedStorageMutationLease mutationLease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(mutationLease);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            cancellationToken);
        ValidateShadowSetCore(operation, linked.Token);
    }

    internal string GetStagingDirectory(Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException(
                "Archive split operation id cannot be empty.",
                nameof(operationId));
        }

        return Path.Combine(
            _archiveDirectory,
            $".clipensk-archive-split-{operationId:D}");
    }

    private PendingArchiveSplitOperation BuildCore(
        Guid operationId,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        PendingArchiveSplitOperation operation =
            _pendingSplitRepository.ReadAsync(token).AsTask().GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("No pending archive split exists.");

        if (operation.OperationId != operationId)
        {
            throw new InvalidOperationException(
                "Pending archive split ownership does not match the requested operation.");
        }

        if (operation.Phase != ArchiveSplitPhase.Planned)
        {
            throw new InvalidOperationException(
                "Archive split shadow set can be built only from Planned phase.");
        }

        string sourcePath = GetFinalArchivePath(operation.SourceFileName);
        DatabaseIdentity sourceIdentity = ValidateArchiveDatabase(
            sourcePath,
            operation.SourceFileName,
            operation.SourceDatabaseId,
            operation.SourceCoverage,
            token);

        string stagingDirectory = GetStagingDirectory(operation.OperationId);
        ResetExactStagingDirectory(stagingDirectory, token);

        using SqliteConnection source = OpenDatabase(
            sourcePath,
            SqliteOpenMode.ReadOnly,
            token);

        foreach (PendingArchiveSplitSegment segment in operation.Segments)
        {
            token.ThrowIfCancellationRequested();
            string stagedPath = GetStagedArchivePath(stagingDirectory, segment.FileName);
            DateTimeOffset createdAtUtc = segment.SegmentOrder == 0
                ? sourceIdentity.CreatedAtUtc
                : operation.CreatedAtUtc;

            BuildSegment(
                source,
                stagedPath,
                segment,
                createdAtUtc,
                token);

            _ = ValidateArchiveDatabase(
                stagedPath,
                segment.FileName,
                segment.DatabaseId,
                segment.Coverage,
                token);
        }

        ValidateShadowSetCore(operation, token);
        RecheckFinalArchiveReservations(operation, token);

        return AdvanceToReadyToPublish(operation, token);
    }

    private void BuildSegment(
        SqliteConnection source,
        string stagedPath,
        PendingArchiveSplitSegment segment,
        DateTimeOffset createdAtUtc,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        if (File.Exists(stagedPath) || Directory.Exists(stagedPath))
        {
            throw new InvalidOperationException(
                $"Archive split staged path '{segment.FileName.FileName}' already exists.");
        }

        using SqliteConnection target = OpenDatabase(
            stagedPath,
            SqliteOpenMode.ReadWriteCreate,
            token);
        using SqliteTransaction transaction = target.BeginTransaction();

        ArchiveShadowWriter.CreateArchiveV1Schema(
            target,
            transaction,
            _session.StorageId,
            segment.FileName,
            segment.DatabaseId,
            segment.Coverage,
            createdAtUtc);
        ArchiveShadowWriter.CopyCoverage(
            source,
            target,
            transaction,
            segment.Coverage,
            token);

        token.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private void ValidateShadowSetCore(
        PendingArchiveSplitOperation operation,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string sourcePath = GetFinalArchivePath(operation.SourceFileName);
        _ = ValidateArchiveDatabase(
            sourcePath,
            operation.SourceFileName,
            operation.SourceDatabaseId,
            operation.SourceCoverage,
            token);

        string stagingDirectory = GetStagingDirectory(operation.OperationId);
        if (!Directory.Exists(stagingDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Archive split staging directory '{stagingDirectory}' was not found.");
        }

        using SqliteConnection source = OpenDatabase(
            sourcePath,
            SqliteOpenMode.ReadOnly,
            token);

        foreach (PendingArchiveSplitSegment segment in operation.Segments)
        {
            token.ThrowIfCancellationRequested();
            string stagedPath = GetStagedArchivePath(stagingDirectory, segment.FileName);
            _ = ValidateArchiveDatabase(
                stagedPath,
                segment.FileName,
                segment.DatabaseId,
                segment.Coverage,
                token);

            using SqliteConnection staged = OpenDatabase(
                stagedPath,
                SqliteOpenMode.ReadOnly,
                token);
            ArchiveShadowWriter.CompareCoverage(
                source,
                staged,
                segment.Coverage,
                ArchiveSplitOperationLabel,
                token);
        }
    }

    private DatabaseIdentity ValidateArchiveDatabase(
        string databasePath,
        ArchiveFileName expectedFileName,
        Guid expectedDatabaseId,
        JournalDateRange expectedCoverage,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ArchiveShadowWriter.IsCanonicalArchiveFileName(expectedFileName) ||
            expectedDatabaseId == Guid.Empty)
        {
            throw new ArgumentException("Expected Archive identity is invalid.");
        }

        if (!string.Equals(
                Path.GetFileName(databasePath),
                expectedFileName.FileName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Archive database path does not match the expected canonical filename.");
        }

        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException(
                "Archive database was not found.",
                databasePath);
        }

        using SqliteConnection connection = OpenDatabase(
            databasePath,
            SqliteOpenMode.ReadOnly,
            token);

        return ArchiveShadowWriter.ValidateShadowDatabase(
            connection,
            _session.StorageId,
            expectedFileName,
            expectedDatabaseId,
            expectedCoverage,
            token);
    }

    private void RecheckFinalArchiveReservations(
        PendingArchiveSplitOperation operation,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _ = ValidateArchiveDatabase(
            GetFinalArchivePath(operation.SourceFileName),
            operation.SourceFileName,
            operation.SourceDatabaseId,
            operation.SourceCoverage,
            token);

        for (int index = 1; index < operation.Segments.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            PendingArchiveSplitSegment segment = operation.Segments[index];
            string finalPath = GetFinalArchivePath(segment.FileName);
            if (File.Exists(finalPath) || Directory.Exists(finalPath))
            {
                throw new InvalidOperationException(
                    $"Reserved Archive filename '{segment.FileName.FileName}' is already occupied.");
            }
        }
    }

    private PendingArchiveSplitOperation AdvanceToReadyToPublish(
        PendingArchiveSplitOperation operation,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using SqliteConnection connection = OpenDatabase(
            _currentDatabasePath,
            SqliteOpenMode.ReadWrite,
            token);
        using SqliteTransaction transaction = connection.BeginTransaction();

        PendingArchiveSplitOperation updated =
            SqlitePendingArchiveSplitRepository.AdvancePhaseInTransaction(
                connection,
                transaction,
                operation.OperationId,
                ArchiveSplitPhase.Planned,
                ArchiveSplitPhase.ReadyToPublish,
                token);

        token.ThrowIfCancellationRequested();
        transaction.Commit();

        return updated;
    }

    private SqliteConnection OpenDatabase(
        string databasePath,
        SqliteOpenMode mode,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ReadOnlyMemory<byte> masterKey = _session.DangerousGetMasterKeyMemory();
        SqliteConnection connection = _connectionFactory.Open(
            databasePath,
            masterKey,
            mode);
        try
        {
            EnableForeignKeys(connection);
            token.ThrowIfCancellationRequested();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void ResetExactStagingDirectory(
        string stagingDirectory,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (File.Exists(stagingDirectory))
        {
            throw new InvalidOperationException(
                "Archive split staging path is occupied by a file.");
        }

        if (Directory.Exists(stagingDirectory))
        {
            FileAttributes attributes = File.GetAttributes(stagingDirectory);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "Archive split staging directory cannot be a reparse point.");
            }

            Directory.Delete(stagingDirectory, recursive: true);
        }

        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(stagingDirectory);
    }

    private string GetFinalArchivePath(ArchiveFileName fileName)
    {
        if (!ArchiveShadowWriter.IsCanonicalArchiveFileName(fileName))
        {
            throw new InvalidDataException(
                "Archive split plan contains a noncanonical filename.");
        }

        return Path.Combine(_archiveDirectory, fileName.FileName);
    }

    private static string GetStagedArchivePath(
        string stagingDirectory,
        ArchiveFileName fileName)
    {
        if (!ArchiveShadowWriter.IsCanonicalArchiveFileName(fileName))
        {
            throw new InvalidDataException(
                "Archive split plan contains a noncanonical filename.");
        }

        return Path.Combine(stagingDirectory, fileName.FileName);
    }

    private static void EnableForeignKeys(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
    }

    private static string FormatDate(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

}
