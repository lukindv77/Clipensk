using System.Globalization;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Databases;

/// <summary>
/// Result of scanning the eligible Current window for automatic Archive rotation.
/// <see cref="EligibleDays"/> is chronologically ordered and contiguous: zero-record dates inside
/// the window are present with <c>RecordCount = 0</c> because they count toward calendar span and
/// toward assigned coverage. An empty list means rotation is a no-op.
/// </summary>
public sealed record ArchiveRotationSourceScan(
    IReadOnlyList<ArchiveRotationDayMetrics> EligibleDays,
    DateOnly? AssignedArchiveCoverageEnd,
    int NextArchiveBaseNumber);

/// <summary>
/// Reads the closed Current suffix that automatic rotation may consider, per
/// <c>docs/ARCHIVE_ROTATION_PROTOCOL.md</c> §3 and §4.
///
/// The scanner is read-only, but it still runs under the storage mutation gate: a concurrent
/// capture or maintenance write would invalidate the day metrics it returns. Both a self-serializing
/// entry point and a lease-aware core are exposed, so a later rotation coordinator that already
/// holds the lease can reuse this scan instead of nesting a second lease acquisition.
///
/// Catalog validation is intentionally not performed here. Catalog is a rebuildable projection and
/// its validation belongs to the rotation coordinator that also owns publication.
/// </summary>
public sealed class ProtectedArchiveRotationSourceScanner
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly ProtectedArchiveDatabaseService _archiveService;
    private readonly SqlitePendingArchiveRotationRepository _pendingRotationRepository;
    private readonly SqlitePendingArchiveSplitRepository _pendingSplitRepository;
    private readonly string _archiveDirectory;
    private readonly string _currentDatabasePath;

    public ProtectedArchiveRotationSourceScanner(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
        _archiveService = new ProtectedArchiveDatabaseService(session, _connectionFactory);
        _pendingRotationRepository = new SqlitePendingArchiveRotationRepository(
            session,
            _connectionFactory);
        _pendingSplitRepository = new SqlitePendingArchiveSplitRepository(
            session,
            _connectionFactory);
        string dataRootPath = Path.GetFullPath(session.DataRootPath);
        _archiveDirectory = Path.Combine(dataRootPath, "Archive");
        _currentDatabasePath = Path.Combine(dataRootPath, "Current", "current.db");
    }

    public async Task<ArchiveRotationSourceScan> ScanAsync(
        DateOnly currentLocalDate,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            cancellationToken);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();

        using ProtectedStorageMutationLease mutationLease =
            await _session.AcquireMutationLeaseAsync(token).ConfigureAwait(false);

        return await Task.Run(
            () => ScanCore(currentLocalDate, token),
            CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Lease-aware entry point for callers that already hold the storage mutation lease.
    /// </summary>
    internal ArchiveRotationSourceScan Scan(
        DateOnly currentLocalDate,
        ProtectedStorageMutationLease mutationLease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutationLease);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            cancellationToken);
        return ScanCore(currentLocalDate, linked.Token);
    }

    /// <summary>
    /// Allocates consecutive canonical unsplit base numbers starting at <paramref name="nextBaseNumber"/>.
    /// Gaps are never reused, so allocation stays monotonic and an old gap can never be mistaken for
    /// space a crashed operation may overwrite.
    /// </summary>
    public static IReadOnlyList<ArchiveFileName> AllocateBaseFileNames(int nextBaseNumber, int count)
    {
        if (nextBaseNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nextBaseNumber),
                "Archive base number must be positive.");
        }
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count),
                "Archive base name count cannot be negative.");
        }
        if (count > ArchiveFileName.MaxBaseNumber - nextBaseNumber + 1)
        {
            throw new InvalidOperationException(
                "Canonical Archive base-number namespace is exhausted.");
        }

        var names = new ArchiveFileName[count];
        for (int index = 0; index < count; index++)
        {
            names[index] = new ArchiveFileName(nextBaseNumber + index, ArchiveFileName.NoSplit);
        }
        return names;
    }

    private ArchiveRotationSourceScan ScanCore(DateOnly currentLocalDate, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RejectActivePendingOperations(token);

        ArchiveCoverageSurvey survey = SurveyArchiveSet(token);
        IReadOnlyList<ArchiveRotationDayMetrics> eligibleDays = ReadEligibleCurrentDays(
            currentLocalDate,
            survey.AssignedCoverageEnd,
            token);

        token.ThrowIfCancellationRequested();
        return new ArchiveRotationSourceScan(
            eligibleDays,
            survey.AssignedCoverageEnd,
            survey.NextBaseNumber);
    }

    private void RejectActivePendingOperations(CancellationToken token)
    {
        if (_pendingRotationRepository.ReadAsync(token).AsTask().GetAwaiter().GetResult() is not null)
        {
            throw new InvalidOperationException(
                "A pending archive rotation blocks planning a new rotation.");
        }

        if (_pendingSplitRepository.ReadAsync(token).AsTask().GetAwaiter().GetResult() is not null)
        {
            throw new InvalidOperationException(
                "A pending archive split blocks planning archive rotation.");
        }
    }

    private ArchiveCoverageSurvey SurveyArchiveSet(CancellationToken token)
    {
        if (!Directory.Exists(_archiveDirectory))
        {
            throw new DirectoryNotFoundException("Archive directory was not found.");
        }

        var coverages = new List<JournalDateRange>();
        int maxBaseNumber = 0;
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

            DatabaseIdentity identity = _archiveService
                .ValidateAsync(parsedFileName, token)
                .GetAwaiter()
                .GetResult();
            if (identity.CoverageStartDate is not DateOnly coverageStart ||
                identity.CoverageEndDate is not DateOnly coverageEnd)
            {
                throw new InvalidDataException(
                    $"Archive '{persistedFileName}' does not have assigned coverage.");
            }

            // Split family members share a base number, so the maximum is taken across every
            // canonical file rather than across unsplit files only.
            maxBaseNumber = Math.Max(maxBaseNumber, parsedFileName.BaseNumber);
            coverages.Add(new JournalDateRange(coverageStart, coverageEnd));
        }

        coverages.Sort(static (left, right) => left.StartDate.CompareTo(right.StartDate));
        for (int index = 1; index < coverages.Count; index++)
        {
            if (coverages[index].Intersects(coverages[index - 1]))
            {
                throw new InvalidDataException(
                    "Physical Archive coverage overlaps and cannot be used for rotation planning.");
            }
        }

        if (maxBaseNumber >= ArchiveFileName.MaxBaseNumber)
        {
            throw new InvalidOperationException(
                "Canonical Archive base-number namespace is exhausted.");
        }

        DateOnly? assignedCoverageEnd = coverages.Count == 0
            ? null
            : coverages[^1].EndDate;
        return new ArchiveCoverageSurvey(assignedCoverageEnd, maxBaseNumber + 1);
    }

    private IReadOnlyList<ArchiveRotationDayMetrics> ReadEligibleCurrentDays(
        DateOnly currentLocalDate,
        DateOnly? assignedCoverageEnd,
        CancellationToken token)
    {
        using SqliteConnection connection = OpenValidatedCurrent(token);
        var dayCounts = new SortedList<DateOnly, long>();

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT CalendarDate, COUNT(*)
                FROM ClipboardHistoryEvent
                WHERE CalendarDate < $currentLocalDate
                GROUP BY CalendarDate
                ORDER BY CalendarDate;
                """;
            command.Parameters.AddWithValue("$currentLocalDate", FormatDate(currentLocalDate));
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (!TryParseDate(reader.GetString(0), out DateOnly calendarDate))
                {
                    throw new InvalidDataException(
                        "Current clipboard history contains a non-canonical CalendarDate.");
                }

                // Automatic rotation only ever considers the chronological Current suffix after
                // existing Archive ownership. A closed row at or before the last assigned coverage
                // end means an earlier transfer did not finish, or that Current holds history in a
                // gap ahead of an existing Archive; planning over either would risk overlapping
                // coverage, so both fail closed here and are left to the transfer/recovery path.
                if (assignedCoverageEnd is DateOnly ownedThrough && calendarDate <= ownedThrough)
                {
                    throw new InvalidDataException(
                        "Automatic rotation requires Current history strictly after assigned Archive coverage.");
                }

                dayCounts.Add(calendarDate, reader.GetInt64(1));
            }
        }

        if (dayCounts.Count == 0)
        {
            return Array.Empty<ArchiveRotationDayMetrics>();
        }

        DateOnly windowStart = dayCounts.Keys[0];
        DateOnly windowEnd = dayCounts.Keys[^1];
        int dayCount = windowEnd.DayNumber - windowStart.DayNumber + 1;
        var days = new ArchiveRotationDayMetrics[dayCount];
        for (int offset = 0; offset < dayCount; offset++)
        {
            token.ThrowIfCancellationRequested();
            DateOnly calendarDate = windowStart.AddDays(offset);
            days[offset] = new ArchiveRotationDayMetrics(
                calendarDate,
                dayCounts.TryGetValue(calendarDate, out long recordCount) ? recordCount : 0);
        }

        return days;
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
            SqliteOpenMode.ReadOnly);
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
                        "Archive rotation scanning requires the expected Current identity.");
                }

                version = reader.GetInt32(3);
                if (reader.Read())
                {
                    throw new InvalidDataException(
                        "Archive rotation scanning requires exactly one Current identity row.");
                }
            }

            if (version != ProtectedStorageDatabaseService.CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    "Archive rotation scanning requires the active Current schema version.");
            }

            using (SqliteCommand userVersion = connection.CreateCommand())
            {
                userVersion.CommandText = "PRAGMA user_version;";
                if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) != version)
                {
                    throw new InvalidDataException(
                        "Current user_version does not match its identity.");
                }
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

    private static bool TryParseDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);

    private sealed record ArchiveCoverageSurvey(
        DateOnly? AssignedCoverageEnd,
        int NextBaseNumber);
}
