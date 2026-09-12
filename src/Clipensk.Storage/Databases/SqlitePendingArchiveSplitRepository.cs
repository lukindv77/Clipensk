using System.Globalization;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Databases;

public sealed class SqlitePendingArchiveSplitRepository
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly string _currentDatabasePath;

    public SqlitePendingArchiveSplitRepository(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
        _currentDatabasePath = Path.Combine(
            Path.GetFullPath(session.DataRootPath),
            "Current",
            "current.db");
    }

    public ValueTask<PendingArchiveSplitOperation?> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked = CreateLinkedCancellation(cancellationToken);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();

        using SqliteConnection connection = OpenValidatedCurrent(SqliteOpenMode.ReadOnly, token);
        PendingArchiveSplitOperation? operation = ReadInTransaction(connection, null, token);
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(operation);
    }

    public async ValueTask<PendingArchiveSplitOperation> StartAsync(
        ArchiveFileName sourceFileName,
        Guid sourceDatabaseId,
        JournalDateRange sourceCoverage,
        IReadOnlyList<PendingArchiveSplitSegment> segments,
        CancellationToken cancellationToken = default)
    {
        ValidatePlan(sourceFileName, sourceDatabaseId, sourceCoverage, segments);

        using CancellationTokenSource linked = CreateLinkedCancellation(cancellationToken);
        CancellationToken token = linked.Token;
        using ProtectedStorageMutationLease mutationLease =
            await _session.AcquireMutationLeaseAsync(token).ConfigureAwait(false);

        using SqliteConnection connection = OpenValidatedCurrent(SqliteOpenMode.ReadWrite, token);
        using SqliteTransaction transaction = connection.BeginTransaction();
        PendingArchiveSplitOperation operation = StartInTransaction(
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

    public async ValueTask<PendingArchiveSplitOperation> AdvancePhaseAsync(
        Guid operationId,
        ArchiveSplitPhase expectedPhase,
        ArchiveSplitPhase nextPhase,
        CancellationToken cancellationToken = default)
    {
        ValidateOperationId(operationId, nameof(operationId));
        ValidatePhaseTransition(expectedPhase, nextPhase);

        using CancellationTokenSource linked = CreateLinkedCancellation(cancellationToken);
        CancellationToken token = linked.Token;
        using ProtectedStorageMutationLease mutationLease =
            await _session.AcquireMutationLeaseAsync(token).ConfigureAwait(false);

        using SqliteConnection connection = OpenValidatedCurrent(SqliteOpenMode.ReadWrite, token);
        using SqliteTransaction transaction = connection.BeginTransaction();
        PendingArchiveSplitOperation updated = AdvancePhaseInTransaction(
            connection,
            transaction,
            operationId,
            expectedPhase,
            nextPhase,
            token);
        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return updated;
    }

    public async ValueTask ClearCompletedAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        ValidateOperationId(operationId, nameof(operationId));

        using CancellationTokenSource linked = CreateLinkedCancellation(cancellationToken);
        CancellationToken token = linked.Token;
        using ProtectedStorageMutationLease mutationLease =
            await _session.AcquireMutationLeaseAsync(token).ConfigureAwait(false);

        using SqliteConnection connection = OpenValidatedCurrent(SqliteOpenMode.ReadWrite, token);
        using SqliteTransaction transaction = connection.BeginTransaction();
        ClearCompletedInTransaction(connection, transaction, operationId, token);
        token.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    internal static PendingArchiveSplitOperation StartInTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ArchiveFileName sourceFileName,
        Guid sourceDatabaseId,
        JournalDateRange sourceCoverage,
        IReadOnlyList<PendingArchiveSplitSegment> segments,
        Guid operationId,
        DateTimeOffset createdAtUtc,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ValidateOperationId(operationId, nameof(operationId));
        ValidateUtc(createdAtUtc, nameof(createdAtUtc));
        ValidatePlan(sourceFileName, sourceDatabaseId, sourceCoverage, segments);
        token.ThrowIfCancellationRequested();

        if (ReadInTransaction(connection, transaction, token) is not null)
        {
            throw new InvalidOperationException("A pending archive split already exists.");
        }

        using (SqliteCommand operation = connection.CreateCommand())
        {
            operation.Transaction = transaction;
            operation.CommandText = """
                INSERT INTO PendingArchiveSplit (
                    SingletonId, OperationId, SourceFileName, SourceDatabaseId,
                    SourceCoverageStartDate, SourceCoverageEndDate, Phase, CreatedAtUtc)
                VALUES (1, $operationId, $sourceFileName, $sourceDatabaseId,
                    $coverageStart, $coverageEnd, $phase, $createdAtUtc);
                """;
            operation.Parameters.AddWithValue("$operationId", operationId.ToString("D"));
            operation.Parameters.AddWithValue("$sourceFileName", sourceFileName.FileName);
            operation.Parameters.AddWithValue("$sourceDatabaseId", sourceDatabaseId.ToString("D"));
            operation.Parameters.AddWithValue("$coverageStart", FormatDate(sourceCoverage.StartDate));
            operation.Parameters.AddWithValue("$coverageEnd", FormatDate(sourceCoverage.EndDate));
            operation.Parameters.AddWithValue("$phase", (int)ArchiveSplitPhase.Planned);
            operation.Parameters.AddWithValue("$createdAtUtc", FormatUtc(createdAtUtc));
            operation.ExecuteNonQuery();
        }

        foreach (PendingArchiveSplitSegment segment in segments)
        {
            token.ThrowIfCancellationRequested();
            using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO PendingArchiveSplitSegment (
                    OperationId, SegmentOrder, FileName, DatabaseId,
                    CoverageStartDate, CoverageEndDate)
                VALUES ($operationId, $segmentOrder, $fileName, $databaseId,
                    $coverageStart, $coverageEnd);
                """;
            insert.Parameters.AddWithValue("$operationId", operationId.ToString("D"));
            insert.Parameters.AddWithValue("$segmentOrder", segment.SegmentOrder);
            insert.Parameters.AddWithValue("$fileName", segment.FileName.FileName);
            insert.Parameters.AddWithValue("$databaseId", segment.DatabaseId.ToString("D"));
            insert.Parameters.AddWithValue("$coverageStart", FormatDate(segment.Coverage.StartDate));
            insert.Parameters.AddWithValue("$coverageEnd", FormatDate(segment.Coverage.EndDate));
            insert.ExecuteNonQuery();
        }

        return new PendingArchiveSplitOperation(
            operationId,
            sourceFileName,
            sourceDatabaseId,
            sourceCoverage,
            ArchiveSplitPhase.Planned,
            createdAtUtc,
            segments.ToArray());
    }

    internal static PendingArchiveSplitOperation AdvancePhaseInTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid operationId,
        ArchiveSplitPhase expectedPhase,
        ArchiveSplitPhase nextPhase,
        CancellationToken token)
    {
        ValidateOperationId(operationId, nameof(operationId));
        ValidatePhaseTransition(expectedPhase, nextPhase);
        PendingArchiveSplitOperation current = ReadRequired(connection, transaction, operationId, token);
        if (current.Phase != expectedPhase)
        {
            throw new InvalidOperationException("Pending archive split phase changed before update.");
        }

        using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE PendingArchiveSplit
            SET Phase = $nextPhase
            WHERE SingletonId = 1
              AND OperationId = $operationId COLLATE BINARY
              AND Phase = $expectedPhase;
            """;
        update.Parameters.AddWithValue("$nextPhase", (int)nextPhase);
        update.Parameters.AddWithValue("$operationId", operationId.ToString("D"));
        update.Parameters.AddWithValue("$expectedPhase", (int)expectedPhase);
        if (update.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("Pending archive split changed before phase update.");
        }

        return current with { Phase = nextPhase };
    }

    internal static void ClearCompletedInTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid operationId,
        CancellationToken token)
    {
        PendingArchiveSplitOperation current = ReadRequired(connection, transaction, operationId, token);
        if (current.Phase != ArchiveSplitPhase.CatalogPublished)
        {
            throw new InvalidOperationException(
                "Pending archive split can be cleared only after CatalogPublished.");
        }

        using SqliteCommand delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = """
            DELETE FROM PendingArchiveSplit
            WHERE SingletonId = 1 AND OperationId = $operationId COLLATE BINARY;
            """;
        delete.Parameters.AddWithValue("$operationId", operationId.ToString("D"));
        if (delete.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("Pending archive split changed before clear.");
        }
    }

    internal static PendingArchiveSplitOperation? ReadInTransaction(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken token)
    {
        using SqliteCommand operationCommand = connection.CreateCommand();
        operationCommand.Transaction = transaction;
        operationCommand.CommandText = """
            SELECT SingletonId, OperationId, SourceFileName, SourceDatabaseId,
                   SourceCoverageStartDate, SourceCoverageEndDate, Phase, CreatedAtUtc
            FROM PendingArchiveSplit
            ORDER BY SingletonId
            LIMIT 2;
            """;

        using SqliteDataReader operationReader = operationCommand.ExecuteReader();
        if (!operationReader.Read())
        {
            return null;
        }

        if (operationReader.GetInt64(0) != 1 ||
            !Guid.TryParseExact(operationReader.GetString(1), "D", out Guid operationId) ||
            operationId == Guid.Empty ||
            !ArchiveFileName.TryParse(operationReader.GetString(2), out ArchiveFileName sourceFileName) ||
            !string.Equals(sourceFileName.FileName, operationReader.GetString(2), StringComparison.Ordinal) ||
            !Guid.TryParseExact(operationReader.GetString(3), "D", out Guid sourceDatabaseId) ||
            sourceDatabaseId == Guid.Empty ||
            !TryParseDate(operationReader.GetString(4), out DateOnly sourceStart) ||
            !TryParseDate(operationReader.GetString(5), out DateOnly sourceEnd) ||
            sourceEnd < sourceStart ||
            !Enum.IsDefined((ArchiveSplitPhase)operationReader.GetInt32(6)))
        {
            throw new InvalidDataException("Pending archive split operation state is invalid.");
        }

        ArchiveSplitPhase phase = (ArchiveSplitPhase)operationReader.GetInt32(6);
        DateTimeOffset createdAtUtc = ParseUtc(operationReader.GetString(7));
        if (operationReader.Read())
        {
            throw new InvalidDataException("PendingArchiveSplit contains more than one operation.");
        }
        operationReader.Close();

        var segments = new List<PendingArchiveSplitSegment>();
        using SqliteCommand segmentCommand = connection.CreateCommand();
        segmentCommand.Transaction = transaction;
        segmentCommand.CommandText = """
            SELECT SegmentOrder, FileName, DatabaseId, CoverageStartDate, CoverageEndDate
            FROM PendingArchiveSplitSegment
            WHERE OperationId = $operationId COLLATE BINARY
            ORDER BY SegmentOrder;
            """;
        segmentCommand.Parameters.AddWithValue("$operationId", operationId.ToString("D"));
        using SqliteDataReader segmentReader = segmentCommand.ExecuteReader();
        while (segmentReader.Read())
        {
            if (!ArchiveFileName.TryParse(segmentReader.GetString(1), out ArchiveFileName fileName) ||
                !string.Equals(fileName.FileName, segmentReader.GetString(1), StringComparison.Ordinal) ||
                !Guid.TryParseExact(segmentReader.GetString(2), "D", out Guid databaseId) ||
                databaseId == Guid.Empty ||
                !TryParseDate(segmentReader.GetString(3), out DateOnly start) ||
                !TryParseDate(segmentReader.GetString(4), out DateOnly end) ||
                end < start)
            {
                throw new InvalidDataException("Pending archive split segment state is invalid.");
            }

            segments.Add(new PendingArchiveSplitSegment(
                segmentReader.GetInt32(0),
                fileName,
                databaseId,
                new JournalDateRange(start, end)));
        }

        var sourceCoverage = new JournalDateRange(sourceStart, sourceEnd);
        ValidatePersistedPlan(sourceFileName, sourceDatabaseId, sourceCoverage, segments);
        token.ThrowIfCancellationRequested();
        return new PendingArchiveSplitOperation(
            operationId,
            sourceFileName,
            sourceDatabaseId,
            sourceCoverage,
            phase,
            createdAtUtc,
            segments.ToArray());
    }

    private static PendingArchiveSplitOperation ReadRequired(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid operationId,
        CancellationToken token)
    {
        PendingArchiveSplitOperation? current = ReadInTransaction(connection, transaction, token);
        if (current is null)
        {
            throw new InvalidOperationException("No pending archive split exists.");
        }
        if (current.OperationId != operationId)
        {
            throw new InvalidOperationException("Pending archive split ownership does not match.");
        }
        return current;
    }

    private SqliteConnection OpenValidatedCurrent(SqliteOpenMode mode, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_session.IsActive)
        {
            throw new OperationCanceledException(_session.CancellationToken);
        }

        SqliteConnection connection = _connectionFactory.Open(
            _currentDatabasePath,
            _session.DangerousGetMasterKeyMemory(),
            mode);
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
                identity.CommandText = "SELECT SingletonId, StorageId, DatabaseRole, SchemaVersion FROM DatabaseIdentity;";
                using SqliteDataReader reader = identity.ExecuteReader();
                if (!reader.Read() ||
                    reader.GetInt64(0) != 1 ||
                    !Guid.TryParse(reader.GetString(1), out Guid storageId) ||
                    storageId != _session.StorageId ||
                    !string.Equals(reader.GetString(2), DatabaseRole.Current.ToString(), StringComparison.Ordinal) ||
                    reader.Read())
                {
                    throw new InvalidDataException("Pending archive split requires the expected Current identity.");
                }
                version = reader.GetInt32(3);
            }

            if (version < PendingArchiveSplitSqlSchema.MinimumCurrentSchemaVersion)
            {
                throw new InvalidDataException("Pending archive split requires Current schema v9 or later.");
            }

            using (SqliteCommand userVersion = connection.CreateCommand())
            {
                userVersion.CommandText = "PRAGMA user_version;";
                if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) != version)
                {
                    throw new InvalidDataException("Current user_version does not match its identity.");
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

    private CancellationTokenSource CreateLinkedCancellation(CancellationToken callerToken) =>
        CancellationTokenSource.CreateLinkedTokenSource(_session.CancellationToken, callerToken);

    private static void ValidatePlan(
        ArchiveFileName sourceFileName,
        Guid sourceDatabaseId,
        JournalDateRange sourceCoverage,
        IReadOnlyList<PendingArchiveSplitSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (sourceFileName.BaseNumber <= 0 || sourceDatabaseId == Guid.Empty)
        {
            throw new ArgumentException("Archive split source identity is invalid.");
        }
        if (segments.Count < 2)
        {
            throw new ArgumentException("Archive split requires at least two result segments.", nameof(segments));
        }

        var fileNames = new HashSet<string>(StringComparer.Ordinal);
        var databaseIds = new HashSet<Guid>();
        DateOnly expectedStart = sourceCoverage.StartDate;
        for (int index = 0; index < segments.Count; index++)
        {
            PendingArchiveSplitSegment segment = segments[index];
            if (segment.SegmentOrder != index ||
                segment.FileName.BaseNumber != sourceFileName.BaseNumber ||
                segment.DatabaseId == Guid.Empty ||
                segment.Coverage.StartDate != expectedStart ||
                !fileNames.Add(segment.FileName.FileName) ||
                !databaseIds.Add(segment.DatabaseId))
            {
                throw new ArgumentException("Archive split plan is not canonical or contiguous.", nameof(segments));
            }

            if (index == 0)
            {
                if (segment.FileName != sourceFileName || segment.DatabaseId != sourceDatabaseId)
                {
                    throw new ArgumentException("First split segment must preserve source filename and DatabaseId.", nameof(segments));
                }
            }
            else if (segment.FileName.SplitSequence <= 0 || segment.DatabaseId == sourceDatabaseId)
            {
                throw new ArgumentException("Additional split segments require new canonical identity.", nameof(segments));
            }

            expectedStart = segment.Coverage.EndDate.AddDays(1);
        }

        if (segments[0].Coverage.StartDate != sourceCoverage.StartDate ||
            segments[^1].Coverage.EndDate != sourceCoverage.EndDate)
        {
            throw new ArgumentException("Archive split plan must exactly partition source coverage.", nameof(segments));
        }
    }

    private static void ValidatePersistedPlan(
        ArchiveFileName sourceFileName,
        Guid sourceDatabaseId,
        JournalDateRange sourceCoverage,
        IReadOnlyList<PendingArchiveSplitSegment> segments)
    {
        try
        {
            ValidatePlan(sourceFileName, sourceDatabaseId, sourceCoverage, segments);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Persisted archive split plan is invalid.", exception);
        }
    }

    private static void ValidatePhaseTransition(ArchiveSplitPhase expected, ArchiveSplitPhase next)
    {
        if (!Enum.IsDefined(expected) || !Enum.IsDefined(next) || (int)next != (int)expected + 1)
        {
            throw new ArgumentException("Archive split phase must advance exactly one step.");
        }
    }

    private static void ValidateOperationId(Guid operationId, string parameterName)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Operation id cannot be empty.");
        }
    }

    private static void ValidateUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must use UTC offset zero.", parameterName);
        }
    }

    private static string FormatDate(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static bool TryParseDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseUtc(string value)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset parsed) ||
            parsed.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("Pending archive split timestamp is not canonical UTC.");
        }
        return parsed;
    }
}
