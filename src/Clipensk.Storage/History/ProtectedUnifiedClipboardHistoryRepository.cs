using System.Globalization;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.History;

public sealed class ProtectedUnifiedClipboardHistoryRepository : IUnifiedClipboardHistoryRepository
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly SqliteCurrentClipboardHistoryRepository _currentRepository;
    private readonly ProtectedArchiveSegmentCatalog _archiveCatalog;
    private readonly StorageQueryPlanner _queryPlanner = new();
    private readonly string _currentDatabasePath;
    private readonly string _archiveDirectoryPath;

    public ProtectedUnifiedClipboardHistoryRepository(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
        if (!session.IsActive)
        {
            throw new InvalidOperationException(
                "Unified history reads require an active protected storage session.");
        }

        string root = Path.GetFullPath(session.DataRootPath);
        _currentDatabasePath = Path.Combine(root, "Current", "current.db");
        _archiveDirectoryPath = Path.Combine(root, "Archive");
        _currentRepository = new SqliteCurrentClipboardHistoryRepository(
            session,
            _connectionFactory);
        _archiveCatalog = new ProtectedArchiveSegmentCatalog(
            session,
            _connectionFactory);
    }

    public ValueTask<IReadOnlyList<UnifiedClipboardHistoryEntry>> ReadAsync(
        JournalDateRange period,
        int limit,
        CancellationToken cancellationToken = default) =>
        ReadCoreAsync(period, limit, before: null, cancellationToken);

    public ValueTask<IReadOnlyList<UnifiedClipboardHistoryEntry>> ReadBeforeAsync(
        JournalDateRange period,
        int limit,
        ClipboardHistoryCursor before,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(before);
        if (before.Period != period)
        {
            throw new ArgumentException(
                "History cursor belongs to a different calendar period.",
                nameof(before));
        }

        return ReadCoreAsync(period, limit, before, cancellationToken);
    }

    private async ValueTask<IReadOnlyList<UnifiedClipboardHistoryEntry>> ReadCoreAsync(
        JournalDateRange period,
        int limit,
        ClipboardHistoryCursor? before,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        using CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            cancellationToken);
        CancellationToken token = linkedCancellation.Token;
        token.ThrowIfCancellationRequested();

        ArchiveSegmentDescriptor[] catalogBefore =
            (await _archiveCatalog.ReadAsync(token).ConfigureAwait(false)).ToArray();
        ValidateArchiveDirectoryMatchesCatalog(catalogBefore, token);

        JournalDateRange? currentAvailableRange = await Task.Run(
            () => ReadCurrentAvailableRange(token),
            CancellationToken.None).ConfigureAwait(false);
        StorageQueryPlan plan = _queryPlanner.Build(
            period,
            new CurrentStoreDescriptor(currentAvailableRange),
            catalogBefore);

        var sourceEntries = new List<LocatedEntry>();

        // Current must be observed before Archive. Current->Archive maintenance commits
        // the Archive copy before purging Current, so this order cannot miss a logical
        // event merely because transfer commits between physical reads.
        if (plan.QueryCurrent)
        {
            IReadOnlyList<ClipboardHistoryEntry> currentEntries = await ReadCurrentPageAsync(
                period,
                limit,
                before,
                token).ConfigureAwait(false);
            foreach (ClipboardHistoryEntry entry in currentEntries)
            {
                sourceEntries.Add(new LocatedEntry(
                    entry,
                    ClipboardHistoryPhysicalLocation.Current));
            }
        }

        foreach (ArchiveSegmentDescriptor segment in plan.ArchiveSegments)
        {
            token.ThrowIfCancellationRequested();
            var archiveRepository = new SqliteArchiveClipboardHistoryRepository(
                _session,
                segment,
                _connectionFactory);
            IReadOnlyList<ClipboardHistoryEntry> archiveEntries = await archiveRepository
                .ReadAsync(period, limit, before, token)
                .ConfigureAwait(false);
            ClipboardHistoryPhysicalLocation location =
                ClipboardHistoryPhysicalLocation.Archive(
                    segment.DatabaseId,
                    segment.FileName);
            foreach (ClipboardHistoryEntry entry in archiveEntries)
            {
                sourceEntries.Add(new LocatedEntry(entry, location));
            }
        }

        IReadOnlyList<UnifiedClipboardHistoryEntry> merged = MergePage(
            sourceEntries,
            period,
            limit);

        // A new/removed archive or a concurrent Catalog rebuild can change which
        // physical files belong to this period. Never return a silently incomplete
        // cross-database page: require the layout projection to remain stable.
        ArchiveSegmentDescriptor[] catalogAfter =
            (await _archiveCatalog.ReadAsync(token).ConfigureAwait(false)).ToArray();
        ValidateArchiveDirectoryMatchesCatalog(catalogAfter, token);
        if (!CatalogProjectionEquals(catalogBefore, catalogAfter))
        {
            throw new InvalidOperationException(
                "Archive catalog projection changed during unified history read; retry the query.");
        }

        token.ThrowIfCancellationRequested();
        return merged;
    }

    private async Task<IReadOnlyList<ClipboardHistoryEntry>> ReadCurrentPageAsync(
        JournalDateRange period,
        int limit,
        ClipboardHistoryCursor? before,
        CancellationToken token)
    {
        return await Task.Run(
            async () =>
            {
                if (before is null)
                {
                    return await _currentRepository
                        .ReadAsync(period, limit, token)
                        .ConfigureAwait(false);
                }

                return await _currentRepository
                    .ReadBeforeAsync(period, limit, before, token)
                    .ConfigureAwait(false);
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    private JournalDateRange? ReadCurrentAvailableRange(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureActiveSession();

        using SqliteConnection connection = _connectionFactory.Open(
            _currentDatabasePath,
            _session.DangerousGetMasterKeyMemory(),
            SqliteOpenMode.ReadOnly);
        ValidateCurrentForPlanning(connection, token);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT MIN(CalendarDate), MAX(CalendarDate) FROM ClipboardHistoryEvent;";
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidDataException(
                "Current history range query did not return an aggregate row.");
        }
        if (reader.IsDBNull(0) && reader.IsDBNull(1))
        {
            token.ThrowIfCancellationRequested();
            return null;
        }
        if (reader.IsDBNull(0) || reader.IsDBNull(1) ||
            !TryParseDate(reader.GetString(0), out DateOnly start) ||
            !TryParseDate(reader.GetString(1), out DateOnly end) ||
            end < start)
        {
            throw new InvalidDataException(
                "Current history contains an invalid persisted calendar range.");
        }

        token.ThrowIfCancellationRequested();
        return new JournalDateRange(start, end);
    }

    private void ValidateCurrentForPlanning(
        SqliteConnection connection,
        CancellationToken token)
    {
        using (SqliteCommand identity = connection.CreateCommand())
        {
            identity.CommandText = """
                SELECT StorageId, DatabaseRole, SchemaVersion
                FROM DatabaseIdentity
                WHERE SingletonId = 1;
                """;
            int schemaVersion;
            using (SqliteDataReader reader = identity.ExecuteReader())
            {
                if (!reader.Read() ||
                    !Guid.TryParse(reader.GetString(0), out Guid storageId) ||
                    storageId != _session.StorageId ||
                    !string.Equals(
                        reader.GetString(1),
                        DatabaseRole.Current.ToString(),
                        StringComparison.Ordinal) ||
                    reader.GetInt32(2) < ClipboardHistorySqlSchema.RequiredCurrentSchemaVersion)
                {
                    throw new InvalidDataException(
                        "Unified history planning requires the expected Current history database.");
                }
                schemaVersion = reader.GetInt32(2);
            }

            using SqliteCommand userVersion = connection.CreateCommand();
            userVersion.CommandText = "PRAGMA user_version;";
            if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) != schemaVersion)
            {
                throw new InvalidDataException(
                    "Current user_version does not match DatabaseIdentity for unified history planning.");
            }
        }

        ClipboardHistorySqlSchema.ValidateTables(connection);
        token.ThrowIfCancellationRequested();
    }

    private void ValidateArchiveDirectoryMatchesCatalog(
        IReadOnlyCollection<ArchiveSegmentDescriptor> catalog,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var physicalNames = new HashSet<string>(StringComparer.Ordinal);
        if (Directory.Exists(_archiveDirectoryPath))
        {
            foreach (string path in Directory.EnumerateFiles(
                         _archiveDirectoryPath,
                         "archive_*.db",
                         SearchOption.TopDirectoryOnly))
            {
                token.ThrowIfCancellationRequested();
                string fileName = Path.GetFileName(path);
                if (!ArchiveFileName.TryParse(fileName, out ArchiveFileName parsed) ||
                    !string.Equals(fileName, parsed.FileName, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Archive file '{fileName}' does not use the canonical Clipensk file name.");
                }
                if (!physicalNames.Add(fileName))
                {
                    throw new InvalidDataException(
                        $"Duplicate archive file name '{fileName}' was discovered.");
                }
            }
        }

        var catalogNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (ArchiveSegmentDescriptor segment in catalog)
        {
            token.ThrowIfCancellationRequested();
            if (!catalogNames.Add(segment.FileName))
            {
                throw new InvalidDataException(
                    $"Catalog contains duplicate archive file name '{segment.FileName}'.");
            }
        }

        if (!physicalNames.SetEquals(catalogNames))
        {
            throw new InvalidDataException(
                "Archive directory and Catalog v3 projection differ; rebuild ArchiveSegmentIndex before reading unified history.");
        }
    }

    private static bool CatalogProjectionEquals(
        IReadOnlyCollection<ArchiveSegmentDescriptor> first,
        IReadOnlyCollection<ArchiveSegmentDescriptor> second)
    {
        if (first.Count != second.Count)
        {
            return false;
        }

        Dictionary<string, ArchiveSegmentDescriptor> secondByFileName = second
            .ToDictionary(item => item.FileName, StringComparer.Ordinal);
        foreach (ArchiveSegmentDescriptor left in first)
        {
            if (!secondByFileName.TryGetValue(left.FileName, out ArchiveSegmentDescriptor? right) ||
                left.DatabaseId != right.DatabaseId ||
                left.Coverage.StartDate != right.Coverage.StartDate ||
                left.Coverage.EndDate != right.Coverage.EndDate ||
                left.IsSealed != right.IsSealed)
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<UnifiedClipboardHistoryEntry> MergePage(
        IEnumerable<LocatedEntry> sourceEntries,
        JournalDateRange period,
        int limit)
    {
        var logical = new Dictionary<Guid, MergeAccumulator>();
        foreach (LocatedEntry located in sourceEntries)
        {
            ClipboardHistoryEntry entry = located.Entry;
            if (!period.Contains(entry.EventTime.CalendarDate))
            {
                throw new InvalidDataException(
                    "A physical history reader returned an event outside the requested calendar period.");
            }

            if (!logical.TryGetValue(entry.EventId, out MergeAccumulator? accumulator))
            {
                accumulator = new MergeAccumulator(entry);
                logical.Add(entry.EventId, accumulator);
            }
            else if (!LogicalEntriesEqual(accumulator.Entry, entry))
            {
                throw new InvalidDataException(
                    $"Logical history event '{entry.EventId:D}' differs between physical databases.");
            }

            if (!accumulator.Locations.Contains(located.Location))
            {
                accumulator.Locations.Add(located.Location);
            }
        }

        UnifiedClipboardHistoryEntry[] page = logical.Values
            .OrderByDescending(item => item.Entry.EventTime.UtcTimestamp)
            .ThenByDescending(
                item => item.Entry.EventId.ToString("D"),
                StringComparer.Ordinal)
            .Take(limit)
            .Select(item => new UnifiedClipboardHistoryEntry(
                item.Entry,
                item.Locations
                    .OrderBy(location => location.Kind)
                    .ThenBy(location => location.FileName, StringComparer.Ordinal)
                    .ThenBy(location => location.DatabaseId)
                    .ToArray()))
            .ToArray();

        return Array.AsReadOnly(page);
    }

    private static bool LogicalEntriesEqual(
        ClipboardHistoryEntry first,
        ClipboardHistoryEntry second)
    {
        if (first.EventId != second.EventId ||
            first.EventTime != second.EventTime ||
            !Equals(first.SourceApplicationId, second.SourceApplicationId) ||
            first.SourceApplication != second.SourceApplication ||
            first.Payloads.Count != second.Payloads.Count)
        {
            return false;
        }

        for (int index = 0; index < first.Payloads.Count; index++)
        {
            if (first.Payloads[index] != second.Payloads[index])
            {
                return false;
            }
        }

        return true;
    }

    private void EnsureActiveSession()
    {
        if (!_session.IsActive)
        {
            throw new OperationCanceledException(_session.CancellationToken);
        }
    }

    private static bool TryParseDate(
        string value,
        out DateOnly date) =>
        DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);

    private sealed record LocatedEntry(
        ClipboardHistoryEntry Entry,
        ClipboardHistoryPhysicalLocation Location);

    private sealed class MergeAccumulator
    {
        public MergeAccumulator(ClipboardHistoryEntry entry)
        {
            Entry = entry;
        }

        public ClipboardHistoryEntry Entry { get; }

        public List<ClipboardHistoryPhysicalLocation> Locations { get; } = new();
    }
}
