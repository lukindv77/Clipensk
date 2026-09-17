using System.Globalization;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Databases;

/// <summary>
/// Validates one user-selected Archive snapshot, persists the immutable split plan while holding
/// the storage mutation lease, and then runs the durable split protocol to completion.
/// </summary>
public sealed class ProtectedArchiveSplitStartService
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly ProtectedArchiveSegmentCatalog _archiveCatalog;
    private readonly ProtectedArchiveSplitRecoveryService _recoveryService;
    private readonly string _currentDatabasePath;

    public ProtectedArchiveSplitStartService(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
        _archiveCatalog = new ProtectedArchiveSegmentCatalog(session, _connectionFactory);
        _recoveryService = new ProtectedArchiveSplitRecoveryService(session, _connectionFactory);
        _currentDatabasePath = Path.Combine(
            Path.GetFullPath(session.DataRootPath),
            "Current",
            "current.db");
    }

    public async Task<IReadOnlyList<ArchiveSegmentDescriptor>> StartAndCompleteAsync(
        ArchiveSegmentDescriptor expectedSource,
        IReadOnlyCollection<JournalDateRange> requestedResultRanges,
        DateOnly currentCalendarDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedSource);
        ArgumentNullException.ThrowIfNull(requestedResultRanges);

        if (!ArchiveFileName.TryParse(expectedSource.FileName, out ArchiveFileName sourceFileName) ||
            !string.Equals(sourceFileName.FileName, expectedSource.FileName, StringComparison.Ordinal) ||
            expectedSource.DatabaseId == Guid.Empty)
        {
            throw new ArgumentException(
                "Archive split source snapshot is invalid.",
                nameof(expectedSource));
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            cancellationToken);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();

        PendingArchiveSplitOperation operation;
        using (ProtectedStorageMutationLease mutationLease =
               await _session.AcquireMutationLeaseAsync(token).ConfigureAwait(false))
        {
            IReadOnlyList<ArchiveSegmentDescriptor> authoritative =
                await _archiveCatalog.ValidateConsistencyAsync(
                    currentCalendarDate,
                    token).ConfigureAwait(false);

            ArchiveSegmentDescriptor source = authoritative.SingleOrDefault(
                descriptor => string.Equals(
                    descriptor.FileName,
                    sourceFileName.FileName,
                    StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    "Selected Archive split source no longer exists in authoritative storage.");

            if (source.DatabaseId != expectedSource.DatabaseId ||
                source.Coverage != expectedSource.Coverage)
            {
                throw new InvalidOperationException(
                    "Selected Archive split source changed since it was loaded.");
            }

            ArchiveFileName[] occupiedFamily = authoritative
                .Select(descriptor => ParseCanonicalFileName(descriptor.FileName))
                .Where(fileName => fileName.BaseNumber == sourceFileName.BaseNumber)
                .ToArray();

            IReadOnlyList<PendingArchiveSplitSegment> segments = new ArchiveSplitPlanner().Build(
                sourceFileName,
                source.DatabaseId,
                source.Coverage,
                requestedResultRanges,
                occupiedFamily,
                Guid.NewGuid);

            operation = PersistPlan(
                sourceFileName,
                source.DatabaseId,
                source.Coverage,
                segments,
                token);
        }

        return await _recoveryService.RecoverAsync(
            operation.OperationId,
            currentCalendarDate,
            cancellationToken).ConfigureAwait(false);
    }

    private PendingArchiveSplitOperation PersistPlan(
        ArchiveFileName sourceFileName,
        Guid sourceDatabaseId,
        JournalDateRange sourceCoverage,
        IReadOnlyList<PendingArchiveSplitSegment> segments,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using SqliteConnection connection = OpenValidatedCurrent(token);
        using SqliteTransaction transaction = connection.BeginTransaction();
        PendingArchiveSplitOperation operation = SqlitePendingArchiveSplitRepository.StartInTransaction(
            connection,
            transaction,
            sourceFileName,
            sourceDatabaseId,
            sourceCoverage,
            segments,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            token);
        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return operation;
    }

    private SqliteConnection OpenValidatedCurrent(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_session.IsActive)
        {
            throw new OperationCanceledException(_session.CancellationToken);
        }

        SqliteConnection connection = _connectionFactory.Open(
            _currentDatabasePath,
            _session.DangerousGetMasterKeyMemory(),
            SqliteOpenMode.ReadWrite);
        try
        {
            using (SqliteCommand foreignKeys = connection.CreateCommand())
            {
                foreignKeys.CommandText = "PRAGMA foreign_keys = ON;";
                foreignKeys.ExecuteNonQuery();
            }

            int version;
            using (SqliteCommand identity = connection.CreateCommand())
            {
                identity.CommandText =
                    "SELECT SingletonId, StorageId, DatabaseRole, SchemaVersion FROM DatabaseIdentity;";
                using SqliteDataReader reader = identity.ExecuteReader();
                if (!reader.Read() ||
                    reader.GetInt64(0) != 1 ||
                    !Guid.TryParse(reader.GetString(1), out Guid storageId) ||
                    storageId != _session.StorageId ||
                    !string.Equals(
                        reader.GetString(2),
                        DatabaseRole.Current.ToString(),
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Archive split planning requires the expected Current identity.");
                }

                version = reader.GetInt32(3);
                if (reader.Read())
                {
                    throw new InvalidDataException(
                        "Archive split planning requires exactly one Current identity row.");
                }
            }

            if (version < PendingArchiveSplitSqlSchema.MinimumCurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    "Archive split planning requires Current schema v9 or later.");
            }

            using (SqliteCommand userVersion = connection.CreateCommand())
            {
                userVersion.CommandText = "PRAGMA user_version;";
                if (Convert.ToInt32(
                        userVersion.ExecuteScalar(),
                        CultureInfo.InvariantCulture) != version)
                {
                    throw new InvalidDataException(
                        "Current user_version does not match its identity.");
                }
            }

            PendingArchiveSplitSqlSchema.ValidateTables(connection);
            token.ThrowIfCancellationRequested();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static ArchiveFileName ParseCanonicalFileName(string fileName)
    {
        if (!ArchiveFileName.TryParse(fileName, out ArchiveFileName parsed) ||
            !string.Equals(parsed.FileName, fileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Archive Catalog contains noncanonical filename '{fileName}'.");
        }

        return parsed;
    }
}
