using System.Globalization;
using Clipensk.Core.History;
using Clipensk.Core.Settings;
using Clipensk.Core.Storage;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Databases;

/// <summary>
/// Durable pending Archive Rotation marker store described by <c>docs/ARCHIVE_ROTATION_PROTOCOL.md</c>.
/// The committed plan is immutable recovery metadata: filenames, planned DatabaseIds, coverage,
/// expected record counts and measured shadow sizes are never recalculated after commit, and the
/// phase may only advance one step forward.
/// </summary>
public sealed class SqlitePendingArchiveRotationRepository
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly string _currentDatabasePath;

    public SqlitePendingArchiveRotationRepository(
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

    public ValueTask<PendingArchiveRotationOperation?> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked = CreateLinkedCancellation(cancellationToken);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();

        using SqliteConnection connection = OpenValidatedCurrent(SqliteOpenMode.ReadOnly, token);
        PendingArchiveRotationOperation? operation = ReadInTransaction(connection, null, token);
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(operation);
    }

    public async ValueTask<PendingArchiveRotationOperation> StartAsync(
        ArchiveRotationSettings policySnapshot,
        IReadOnlyList<PendingArchiveRotationTarget> targets,
        CancellationToken cancellationToken = default)
    {
        ValidatePlan(policySnapshot, targets);

        using CancellationTokenSource linked = CreateLinkedCancellation(cancellationToken);
        CancellationToken token = linked.Token;
        using ProtectedStorageMutationLease mutationLease =
            await _session.AcquireMutationLeaseAsync(token).ConfigureAwait(false);

        using SqliteConnection connection = OpenValidatedCurrent(SqliteOpenMode.ReadWrite, token);
        using SqliteTransaction transaction = connection.BeginTransaction();
        PendingArchiveRotationOperation operation = StartInTransaction(
            connection,
            transaction,
            policySnapshot,
            targets,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            token);
        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return operation;
    }

    public async ValueTask<PendingArchiveRotationOperation> AdvancePhaseAsync(
        Guid operationId,
        ArchiveRotationPhase expectedPhase,
        ArchiveRotationPhase nextPhase,
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
        PendingArchiveRotationOperation updated = AdvancePhaseInTransaction(
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

    internal static PendingArchiveRotationOperation StartInTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ArchiveRotationSettings policySnapshot,
        IReadOnlyList<PendingArchiveRotationTarget> targets,
        Guid operationId,
        DateTimeOffset createdAtUtc,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ValidateOperationId(operationId, nameof(operationId));
        ValidateUtc(createdAtUtc, nameof(createdAtUtc));
        ValidatePlan(policySnapshot, targets);
        token.ThrowIfCancellationRequested();

        if (ReadInTransaction(connection, transaction, token) is not null)
        {
            throw new InvalidOperationException("A pending archive rotation already exists.");
        }

        if (PendingArchiveSplitSqlSchema.HasPendingOperation(connection, transaction))
        {
            throw new InvalidOperationException(
                "A pending archive split blocks starting archive rotation.");
        }

        using (SqliteCommand operation = connection.CreateCommand())
        {
            operation.Transaction = transaction;
            operation.CommandText = """
                INSERT INTO PendingArchiveRotation (
                    SingletonId, OperationId, MaxRecordCount, MaxBytes, MaxCalendarDays,
                    ThresholdMode, Phase, CreatedAtUtc)
                VALUES (1, $operationId, $maxRecordCount, $maxBytes, $maxCalendarDays,
                    $thresholdMode, $phase, $createdAtUtc);
                """;
            operation.Parameters.AddWithValue("$operationId", operationId.ToString("D"));
            operation.Parameters.AddWithValue("$maxRecordCount", ToDbValue(policySnapshot.MaxRecordCount));
            operation.Parameters.AddWithValue("$maxBytes", ToDbValue(policySnapshot.MaxBytes));
            operation.Parameters.AddWithValue("$maxCalendarDays", ToDbValue(policySnapshot.MaxCalendarDays));
            operation.Parameters.AddWithValue(
                "$thresholdMode",
                ToDbValue((int?)policySnapshot.ThresholdMode));
            operation.Parameters.AddWithValue("$phase", (int)ArchiveRotationPhase.Planned);
            operation.Parameters.AddWithValue("$createdAtUtc", FormatUtc(createdAtUtc));
            operation.ExecuteNonQuery();
        }

        foreach (PendingArchiveRotationTarget target in targets)
        {
            token.ThrowIfCancellationRequested();
            using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO PendingArchiveRotationTarget (
                    OperationId, SegmentOrder, FileName, DatabaseId,
                    CoverageStartDate, CoverageEndDate, ExpectedRecordCount, ShadowPhysicalSizeBytes)
                VALUES ($operationId, $segmentOrder, $fileName, $databaseId,
                    $coverageStart, $coverageEnd, $expectedRecordCount, $shadowPhysicalSizeBytes);
                """;
            insert.Parameters.AddWithValue("$operationId", operationId.ToString("D"));
            insert.Parameters.AddWithValue("$segmentOrder", target.SegmentOrder);
            insert.Parameters.AddWithValue("$fileName", target.FileName.FileName);
            insert.Parameters.AddWithValue("$databaseId", target.DatabaseId.ToString("D"));
            insert.Parameters.AddWithValue("$coverageStart", FormatDate(target.Coverage.StartDate));
            insert.Parameters.AddWithValue("$coverageEnd", FormatDate(target.Coverage.EndDate));
            insert.Parameters.AddWithValue("$expectedRecordCount", target.ExpectedRecordCount);
            insert.Parameters.AddWithValue(
                "$shadowPhysicalSizeBytes",
                target.ShadowPhysicalSizeBytes);
            insert.ExecuteNonQuery();
        }

        return new PendingArchiveRotationOperation(
            operationId,
            policySnapshot,
            ArchiveRotationPhase.Planned,
            createdAtUtc,
            targets.ToArray());
    }

    internal static PendingArchiveRotationOperation AdvancePhaseInTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid operationId,
        ArchiveRotationPhase expectedPhase,
        ArchiveRotationPhase nextPhase,
        CancellationToken token)
    {
        ValidateOperationId(operationId, nameof(operationId));
        ValidatePhaseTransition(expectedPhase, nextPhase);
        PendingArchiveRotationOperation current = ReadRequired(connection, transaction, operationId, token);
        if (current.Phase != expectedPhase)
        {
            throw new InvalidOperationException("Pending archive rotation phase changed before update.");
        }

        using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE PendingArchiveRotation
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
            throw new InvalidOperationException("Pending archive rotation changed before phase update.");
        }

        return current with { Phase = nextPhase };
    }

    internal static void ClearCompletedInTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid operationId,
        CancellationToken token)
    {
        PendingArchiveRotationOperation current = ReadRequired(connection, transaction, operationId, token);
        if (current.Phase != ArchiveRotationPhase.CatalogPublished)
        {
            throw new InvalidOperationException(
                "Pending archive rotation can be cleared only after CatalogPublished.");
        }

        using SqliteCommand delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = """
            DELETE FROM PendingArchiveRotation
            WHERE SingletonId = 1 AND OperationId = $operationId COLLATE BINARY;
            """;
        delete.Parameters.AddWithValue("$operationId", operationId.ToString("D"));
        if (delete.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("Pending archive rotation changed before clear.");
        }
    }

    internal static PendingArchiveRotationOperation? ReadInTransaction(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken token)
    {
        using SqliteCommand operationCommand = connection.CreateCommand();
        operationCommand.Transaction = transaction;
        operationCommand.CommandText = """
            SELECT SingletonId, OperationId, MaxRecordCount, MaxBytes, MaxCalendarDays,
                   ThresholdMode, Phase, CreatedAtUtc
            FROM PendingArchiveRotation
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
            !Enum.IsDefined((ArchiveRotationPhase)operationReader.GetInt32(6)))
        {
            throw new InvalidDataException("Pending archive rotation operation state is invalid.");
        }

        var policySnapshot = new ArchiveRotationSettings
        {
            MaxRecordCount = operationReader.IsDBNull(2) ? null : (long?)operationReader.GetInt64(2),
            MaxBytes = operationReader.IsDBNull(3) ? null : (long?)operationReader.GetInt64(3),
            MaxCalendarDays = operationReader.IsDBNull(4) ? null : (int?)operationReader.GetInt32(4),
            ThresholdMode = operationReader.IsDBNull(5)
                ? null
                : (ArchiveRotationThresholdMode?)operationReader.GetInt32(5),
        };

        ArchiveRotationPhase phase = (ArchiveRotationPhase)operationReader.GetInt32(6);
        DateTimeOffset createdAtUtc = ParseUtc(operationReader.GetString(7));
        if (operationReader.Read())
        {
            throw new InvalidDataException("PendingArchiveRotation contains more than one operation.");
        }
        operationReader.Close();

        var targets = new List<PendingArchiveRotationTarget>();
        using SqliteCommand targetCommand = connection.CreateCommand();
        targetCommand.Transaction = transaction;
        targetCommand.CommandText = """
            SELECT SegmentOrder, FileName, DatabaseId, CoverageStartDate, CoverageEndDate,
                   ExpectedRecordCount, ShadowPhysicalSizeBytes
            FROM PendingArchiveRotationTarget
            WHERE OperationId = $operationId COLLATE BINARY
            ORDER BY SegmentOrder;
            """;
        targetCommand.Parameters.AddWithValue("$operationId", operationId.ToString("D"));
        using SqliteDataReader targetReader = targetCommand.ExecuteReader();
        while (targetReader.Read())
        {
            if (!ArchiveFileName.TryParse(targetReader.GetString(1), out ArchiveFileName fileName) ||
                !string.Equals(fileName.FileName, targetReader.GetString(1), StringComparison.Ordinal) ||
                !Guid.TryParseExact(targetReader.GetString(2), "D", out Guid databaseId) ||
                databaseId == Guid.Empty ||
                !TryParseDate(targetReader.GetString(3), out DateOnly start) ||
                !TryParseDate(targetReader.GetString(4), out DateOnly end) ||
                end < start)
            {
                throw new InvalidDataException("Pending archive rotation target state is invalid.");
            }

            targets.Add(new PendingArchiveRotationTarget(
                targetReader.GetInt32(0),
                fileName,
                databaseId,
                new JournalDateRange(start, end),
                targetReader.GetInt64(5),
                targetReader.GetInt64(6)));
        }

        ValidatePersistedPlan(policySnapshot, targets);
        token.ThrowIfCancellationRequested();
        return new PendingArchiveRotationOperation(
            operationId,
            policySnapshot,
            phase,
            createdAtUtc,
            targets.ToArray());
    }

    private static PendingArchiveRotationOperation ReadRequired(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid operationId,
        CancellationToken token)
    {
        PendingArchiveRotationOperation? current = ReadInTransaction(connection, transaction, token);
        if (current is null)
        {
            throw new InvalidOperationException("No pending archive rotation exists.");
        }
        if (current.OperationId != operationId)
        {
            throw new InvalidOperationException("Pending archive rotation ownership does not match.");
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
                    !string.Equals(reader.GetString(2), DatabaseRole.Current.ToString(), StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Pending archive rotation requires the expected Current identity.");
                }

                version = reader.GetInt32(3);
                if (reader.Read())
                {
                    throw new InvalidDataException("Pending archive rotation requires exactly one Current identity row.");
                }
            }

            if (version < PendingArchiveRotationSqlSchema.MinimumCurrentSchemaVersion)
            {
                throw new InvalidDataException("Pending archive rotation requires Current schema v10 or later.");
            }

            using (SqliteCommand userVersion = connection.CreateCommand())
            {
                userVersion.CommandText = "PRAGMA user_version;";
                if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) != version)
                {
                    throw new InvalidDataException("Current user_version does not match its identity.");
                }
            }
            PendingArchiveRotationSqlSchema.ValidateTables(connection);
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
        ArchiveRotationSettings policySnapshot,
        IReadOnlyList<PendingArchiveRotationTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(policySnapshot);
        ArgumentNullException.ThrowIfNull(targets);
        policySnapshot.Validate();

        if (targets.Count == 0)
        {
            throw new ArgumentException(
                "Archive rotation requires at least one planned target.",
                nameof(targets));
        }

        var fileNames = new HashSet<string>(StringComparer.Ordinal);
        var databaseIds = new HashSet<Guid>();
        for (int index = 0; index < targets.Count; index++)
        {
            PendingArchiveRotationTarget target = targets[index];
            if (target.SegmentOrder != index ||
                !IsCanonicalBaseArchiveFileName(target.FileName) ||
                target.DatabaseId == Guid.Empty ||
                target.ExpectedRecordCount < 0 ||
                target.ShadowPhysicalSizeBytes <= 0 ||
                !fileNames.Add(target.FileName.FileName) ||
                !databaseIds.Add(target.DatabaseId))
            {
                throw new ArgumentException(
                    "Archive rotation plan target is not canonical.",
                    nameof(targets));
            }

            if (index == 0)
            {
                continue;
            }

            PendingArchiveRotationTarget previous = targets[index - 1];
            if (target.FileName.BaseNumber <= previous.FileName.BaseNumber ||
                target.Coverage.StartDate.DayNumber != previous.Coverage.EndDate.DayNumber + 1)
            {
                throw new ArgumentException(
                    "Archive rotation plan must allocate increasing base numbers over contiguous coverage.",
                    nameof(targets));
            }
        }
    }

    /// <summary>
    /// Rotation only ever creates new unsplit base Archive files. Split suffixes stay reserved for
    /// Archive Split, so a suffixed name in a rotation plan is fail-closed.
    /// </summary>
    private static bool IsCanonicalBaseArchiveFileName(ArchiveFileName fileName) =>
        fileName.SplitSequence == ArchiveFileName.NoSplit &&
        ArchiveFileName.TryParse(fileName.FileName, out ArchiveFileName parsed) &&
        parsed == fileName &&
        string.Equals(parsed.FileName, fileName.FileName, StringComparison.Ordinal);

    private static void ValidatePersistedPlan(
        ArchiveRotationSettings policySnapshot,
        IReadOnlyList<PendingArchiveRotationTarget> targets)
    {
        try
        {
            ValidatePlan(policySnapshot, targets);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Persisted archive rotation plan is invalid.", exception);
        }
    }

    private static void ValidatePhaseTransition(ArchiveRotationPhase expected, ArchiveRotationPhase next)
    {
        if (!Enum.IsDefined(expected) || !Enum.IsDefined(next) || (int)next != (int)expected + 1)
        {
            throw new ArgumentException("Archive rotation phase must advance exactly one step.");
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

    private static object ToDbValue<T>(T? value)
        where T : struct => (object?)value ?? DBNull.Value;

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
            throw new InvalidDataException("Pending archive rotation timestamp is not canonical UTC.");
        }
        return parsed;
    }
}
