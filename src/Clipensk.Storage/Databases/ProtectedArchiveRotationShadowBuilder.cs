using Clipensk.Core.History;
using Clipensk.Core.Settings;
using Clipensk.Core.Storage;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Databases;

/// <summary>
/// Immutable rotation plan produced from validated staging shadows. Every target carries the
/// physical size actually measured on its closed shadow, never an estimate. <see cref="OpenTail"/>
/// is the trailing candidate that never reached the configured rule; its rows stay in Current and
/// its shadow is discarded.
/// </summary>
public sealed record ArchiveRotationShadowPlan(
    Guid OperationId,
    ArchiveRotationSettings PolicySnapshot,
    IReadOnlyList<PendingArchiveRotationTarget> Targets,
    JournalDateRange? OpenTail);

/// <summary>
/// Builds the hidden Archive v1 shadows that an Archive Rotation would publish, per
/// <c>docs/ARCHIVE_ROTATION_PROTOCOL.md</c> §6 and §7, and cross-checks each one against Current
/// per §8.
///
/// Nothing here touches the canonical Archive set: shadows live in an operation-specific hidden
/// staging directory and Current stays the durable source. The caller owns <paramref name="operationId"/>
/// because the protocol builds and validates shadows *before* the durable marker is committed, so
/// staging and marker must already agree on the same operation identity.
/// </summary>
public sealed class ProtectedArchiveRotationShadowBuilder
{
    private const string RotationOperationLabel = "Archive rotation";

    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly ProtectedArchiveRotationSourceScanner _scanner;
    private readonly ArchiveRotationPlanner _planner = new();
    private readonly string _archiveDirectory;
    private readonly string _currentDatabasePath;

    public ProtectedArchiveRotationShadowBuilder(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
        _scanner = new ProtectedArchiveRotationSourceScanner(session, _connectionFactory);
        string dataRootPath = Path.GetFullPath(session.DataRootPath);
        _archiveDirectory = Path.Combine(dataRootPath, "Archive");
        _currentDatabasePath = Path.Combine(dataRootPath, "Current", "current.db");
    }

    public async Task<ArchiveRotationShadowPlan> BuildAsync(
        Guid operationId,
        ArchiveRotationSettings settings,
        DateOnly currentLocalDate,
        CancellationToken cancellationToken = default)
    {
        ValidateOperationId(operationId);
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
            () => BuildCore(operationId, settings, currentLocalDate, mutationLease, token),
            CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Lease-aware entry point for a coordinator that already holds the storage mutation lease.
    /// </summary>
    internal ArchiveRotationShadowPlan Build(
        Guid operationId,
        ArchiveRotationSettings settings,
        DateOnly currentLocalDate,
        ProtectedStorageMutationLease mutationLease,
        CancellationToken cancellationToken = default)
    {
        ValidateOperationId(operationId);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(mutationLease);
        settings.Validate();

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            cancellationToken);
        return BuildCore(operationId, settings, currentLocalDate, mutationLease, linked.Token);
    }

    internal string GetStagingDirectory(Guid operationId)
    {
        ValidateOperationId(operationId);
        return Path.Combine(
            _archiveDirectory,
            $".clipensk-archive-rotation-{operationId:D}");
    }

    internal string GetStagedArchivePath(Guid operationId, ArchiveFileName fileName)
    {
        if (!ArchiveShadowWriter.IsCanonicalArchiveFileName(fileName) ||
            fileName.SplitSequence != ArchiveFileName.NoSplit)
        {
            throw new InvalidDataException(
                "Archive rotation plan contains a noncanonical or split filename.");
        }

        return Path.Combine(GetStagingDirectory(operationId), fileName.FileName);
    }

    /// <summary>
    /// Rebuilds every planned shadow from Current under the immutable plan, for recovery from the
    /// <c>Planned</c> phase where staging may be incomplete but Current still holds every planned
    /// source row.
    ///
    /// The rebuilt shadow must reproduce the physical size recorded when the plan was committed.
    /// That evidence is what later publication compares published copies against, so a rebuild that
    /// cannot reproduce it fails closed instead of quietly invalidating the plan.
    /// </summary>
    internal void RebuildPlannedShadows(
        Guid operationId,
        IReadOnlyList<PendingArchiveRotationTarget> targets,
        ProtectedStorageMutationLease mutationLease,
        CancellationToken cancellationToken = default)
    {
        ValidateOperationId(operationId);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(mutationLease);

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            cancellationToken);
        CancellationToken token = linked.Token;

        ResetExactStagingDirectory(GetStagingDirectory(operationId), token);
        if (targets.Count == 0)
        {
            return;
        }

        using SqliteConnection current = OpenDatabase(
            _currentDatabasePath,
            SqliteOpenMode.ReadOnly,
            token);

        foreach (PendingArchiveRotationTarget target in targets)
        {
            token.ThrowIfCancellationRequested();
            CreateShadow(operationId, target.FileName, target.DatabaseId, target.Coverage, token);
            ExtendShadow(
                operationId,
                target.FileName,
                current,
                target.Coverage,
                target.Coverage,
                token);

            long physicalSize = ValidateAndMeasureShadow(
                operationId,
                target.FileName,
                target.DatabaseId,
                target.Coverage,
                token);
            if (physicalSize != target.ShadowPhysicalSizeBytes)
            {
                throw new InvalidDataException(
                    $"Rebuilt Archive rotation shadow '{target.FileName.FileName}' does not "
                        + "reproduce the planned physical size.");
            }
        }
    }

    /// <summary>
    /// Revalidates every staged shadow against the plan and re-runs the exact Current cross-check.
    /// </summary>
    internal void ValidateShadowSet(
        ArchiveRotationShadowPlan plan,
        ProtectedStorageMutationLease mutationLease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(mutationLease);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            cancellationToken);
        CancellationToken token = linked.Token;

        if (plan.Targets.Count == 0)
        {
            return;
        }

        string stagingDirectory = GetStagingDirectory(plan.OperationId);
        if (!Directory.Exists(stagingDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Archive rotation staging directory '{stagingDirectory}' was not found.");
        }

        using SqliteConnection current = OpenDatabase(_currentDatabasePath, SqliteOpenMode.ReadOnly, token);
        foreach (PendingArchiveRotationTarget target in plan.Targets)
        {
            token.ThrowIfCancellationRequested();
            VerifyStagedTarget(current, plan.OperationId, target, token);
        }
    }

    private ArchiveRotationShadowPlan BuildCore(
        Guid operationId,
        ArchiveRotationSettings settings,
        DateOnly currentLocalDate,
        ProtectedStorageMutationLease mutationLease,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Reuses the lease-aware scanner path instead of acquiring a second mutation lease.
        ArchiveRotationSourceScan scan = _scanner.Scan(currentLocalDate, mutationLease, token);

        string stagingDirectory = GetStagingDirectory(operationId);
        ResetExactStagingDirectory(stagingDirectory, token);

        if (scan.EligibleDays.Count == 0)
        {
            return new ArchiveRotationShadowPlan(
                operationId,
                settings,
                Array.Empty<PendingArchiveRotationTarget>(),
                OpenTail: null);
        }

        using SqliteConnection current = OpenDatabase(
            _currentDatabasePath,
            SqliteOpenMode.ReadOnly,
            token);

        return settings.MaxBytes.HasValue
            ? BuildWithPhysicalSize(operationId, settings, scan, current, token)
            : BuildFromPurePlan(operationId, settings, scan, current, token);
    }

    /// <summary>
    /// Count/day-only configuration: ready ranges are fully decided by the pure planner, so each
    /// shadow is built once for its final range.
    /// </summary>
    private ArchiveRotationShadowPlan BuildFromPurePlan(
        Guid operationId,
        ArchiveRotationSettings settings,
        ArchiveRotationSourceScan scan,
        SqliteConnection current,
        CancellationToken token)
    {
        ArchiveRotationPlan purePlan = _planner.Build(scan.EligibleDays, settings);
        IReadOnlyList<ArchiveFileName> fileNames = ProtectedArchiveRotationSourceScanner
            .AllocateBaseFileNames(scan.NextArchiveBaseNumber, purePlan.ReadyRanges.Count);

        var targets = new List<PendingArchiveRotationTarget>(purePlan.ReadyRanges.Count);
        for (int index = 0; index < purePlan.ReadyRanges.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            JournalDateRange coverage = purePlan.ReadyRanges[index];
            ArchiveFileName fileName = fileNames[index];
            Guid databaseId = Guid.NewGuid();

            CreateShadow(operationId, fileName, databaseId, coverage, token);
            ExtendShadow(operationId, fileName, current, coverage, coverage, token);

            long physicalSize = ValidateAndMeasureShadow(
                operationId,
                fileName,
                databaseId,
                coverage,
                token);
            VerifyStagedCoverage(current, operationId, fileName, coverage, token);

            targets.Add(new PendingArchiveRotationTarget(
                index,
                fileName,
                databaseId,
                coverage,
                CountRecords(scan.EligibleDays, coverage),
                physicalSize));
        }

        return new ArchiveRotationShadowPlan(
            operationId,
            settings,
            targets.AsReadOnly(),
            purePlan.OpenRange);
    }

    /// <summary>
    /// Physical-size configuration (§7.2): the candidate shadow is extended one complete day at a
    /// time and the SQLCipher connection is closed before its `.db` length is read, so the byte
    /// metric is the real file size rather than an estimate.
    /// </summary>
    private ArchiveRotationShadowPlan BuildWithPhysicalSize(
        Guid operationId,
        ArchiveRotationSettings settings,
        ArchiveRotationSourceScan scan,
        SqliteConnection current,
        CancellationToken token)
    {
        var targets = new List<PendingArchiveRotationTarget>();
        int nextBaseNumber = scan.NextArchiveBaseNumber;

        DateOnly? candidateStart = null;
        ArchiveFileName candidateFileName = default;
        Guid candidateDatabaseId = Guid.Empty;
        long candidateRecordCount = 0;
        int candidateDayCount = 0;

        foreach (ArchiveRotationDayMetrics day in scan.EligibleDays)
        {
            token.ThrowIfCancellationRequested();
            if (candidateStart is null)
            {
                candidateStart = day.CalendarDate;
                candidateFileName = ProtectedArchiveRotationSourceScanner
                    .AllocateBaseFileNames(nextBaseNumber, 1)[0];
                candidateDatabaseId = Guid.NewGuid();
                candidateRecordCount = 0;
                candidateDayCount = 0;
                CreateShadow(
                    operationId,
                    candidateFileName,
                    candidateDatabaseId,
                    new JournalDateRange(day.CalendarDate, day.CalendarDate),
                    token);
            }

            var candidateCoverage = new JournalDateRange(candidateStart.Value, day.CalendarDate);
            ExtendShadow(
                operationId,
                candidateFileName,
                current,
                new JournalDateRange(day.CalendarDate, day.CalendarDate),
                candidateCoverage,
                token);
            candidateRecordCount = SaturatingAdd(candidateRecordCount, day.RecordCount);
            candidateDayCount++;

            long physicalSize = ValidateAndMeasureShadow(
                operationId,
                candidateFileName,
                candidateDatabaseId,
                candidateCoverage,
                token);

            if (!settings.HasReachedThresholds(
                    candidateDayCount,
                    candidateRecordCount,
                    physicalSize))
            {
                continue;
            }

            VerifyStagedCoverage(current, operationId, candidateFileName, candidateCoverage, token);
            targets.Add(new PendingArchiveRotationTarget(
                targets.Count,
                candidateFileName,
                candidateDatabaseId,
                candidateCoverage,
                candidateRecordCount,
                physicalSize));

            nextBaseNumber = candidateFileName.BaseNumber + 1;
            candidateStart = null;
        }

        JournalDateRange? openTail = null;
        if (candidateStart is DateOnly openStart)
        {
            // The trailing candidate never reached the rule, so it is not published and its
            // disposable shadow is removed rather than left as an orphan in staging.
            openTail = new JournalDateRange(openStart, scan.EligibleDays[^1].CalendarDate);
            File.Delete(GetStagedArchivePath(operationId, candidateFileName));
        }

        return new ArchiveRotationShadowPlan(
            operationId,
            settings,
            targets.AsReadOnly(),
            openTail);
    }

    private void CreateShadow(
        Guid operationId,
        ArchiveFileName fileName,
        Guid databaseId,
        JournalDateRange initialCoverage,
        CancellationToken token)
    {
        string stagedPath = GetStagedArchivePath(operationId, fileName);
        if (File.Exists(stagedPath) || Directory.Exists(stagedPath))
        {
            throw new InvalidOperationException(
                $"Archive rotation staged path '{fileName.FileName}' already exists.");
        }

        using SqliteConnection target = OpenDatabase(stagedPath, SqliteOpenMode.ReadWriteCreate, token);
        using SqliteTransaction transaction = target.BeginTransaction();
        ArchiveShadowWriter.CreateArchiveV1Schema(
            target,
            transaction,
            _session.StorageId,
            fileName,
            databaseId,
            initialCoverage,
            DateTimeOffset.UtcNow);
        token.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private void ExtendShadow(
        Guid operationId,
        ArchiveFileName fileName,
        SqliteConnection current,
        JournalDateRange copyRange,
        JournalDateRange stagingCoverage,
        CancellationToken token)
    {
        string stagedPath = GetStagedArchivePath(operationId, fileName);
        using SqliteConnection target = OpenDatabase(stagedPath, SqliteOpenMode.ReadWrite, token);
        using SqliteTransaction transaction = target.BeginTransaction();
        ArchiveShadowWriter.CopyCoverage(
            current,
            target,
            transaction,
            copyRange,
            token,
            mergeApplicationRows: true);
        ArchiveShadowWriter.UpdateStagingCoverage(target, transaction, stagingCoverage);
        token.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    /// <summary>
    /// Fully validates the closed shadow and returns its physical `.db` length. The connection is
    /// disposed before the file is measured so the size is not read while SQLite still owns it.
    /// </summary>
    private long ValidateAndMeasureShadow(
        Guid operationId,
        ArchiveFileName fileName,
        Guid databaseId,
        JournalDateRange coverage,
        CancellationToken token)
    {
        string stagedPath = GetStagedArchivePath(operationId, fileName);
        if (Directory.Exists(stagedPath))
        {
            throw new InvalidOperationException(
                $"Archive rotation staged path '{fileName.FileName}' is occupied by a directory.");
        }

        // Naming the missing shadow explicitly keeps a failed rotation diagnosable; opening a
        // missing file would otherwise surface as a bare SQLite open error.
        if (!File.Exists(stagedPath))
        {
            throw new FileNotFoundException(
                $"Archive rotation shadow '{fileName.FileName}' was not found in staging.",
                stagedPath);
        }

        using (SqliteConnection staged = OpenDatabase(stagedPath, SqliteOpenMode.ReadOnly, token))
        {
            _ = ArchiveShadowWriter.ValidateShadowDatabase(
                staged,
                _session.StorageId,
                fileName,
                databaseId,
                coverage,
                token);
        }

        long length = new FileInfo(stagedPath).Length;
        if (length <= 0)
        {
            throw new InvalidDataException(
                "Archive rotation shadow reported a non-positive physical size.");
        }

        return length;
    }

    private void VerifyStagedTarget(
        SqliteConnection current,
        Guid operationId,
        PendingArchiveRotationTarget target,
        CancellationToken token)
    {
        long physicalSize = ValidateAndMeasureShadow(
            operationId,
            target.FileName,
            target.DatabaseId,
            target.Coverage,
            token);
        if (physicalSize != target.ShadowPhysicalSizeBytes)
        {
            throw new InvalidDataException(
                "Archive rotation shadow physical size no longer matches the planned evidence.");
        }

        VerifyStagedCoverage(current, operationId, target.FileName, target.Coverage, token);
    }

    private void VerifyStagedCoverage(
        SqliteConnection current,
        Guid operationId,
        ArchiveFileName fileName,
        JournalDateRange coverage,
        CancellationToken token)
    {
        string stagedPath = GetStagedArchivePath(operationId, fileName);
        using SqliteConnection staged = OpenDatabase(stagedPath, SqliteOpenMode.ReadOnly, token);
        ArchiveShadowWriter.CompareCoverage(
            current,
            staged,
            coverage,
            RotationOperationLabel,
            token);
    }

    private static long CountRecords(
        IReadOnlyList<ArchiveRotationDayMetrics> days,
        JournalDateRange coverage)
    {
        long total = 0;
        foreach (ArchiveRotationDayMetrics day in days)
        {
            if (coverage.Contains(day.CalendarDate))
            {
                total = SaturatingAdd(total, day.RecordCount);
            }
        }
        return total;
    }

    private static long SaturatingAdd(long current, long next) =>
        next > long.MaxValue - current ? long.MaxValue : current + next;

    private static void ResetExactStagingDirectory(string stagingDirectory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (File.Exists(stagingDirectory))
        {
            throw new InvalidOperationException(
                "Archive rotation staging path is occupied by a file.");
        }

        if (Directory.Exists(stagingDirectory))
        {
            Directory.Delete(stagingDirectory, recursive: true);
        }

        Directory.CreateDirectory(stagingDirectory);
    }

    private SqliteConnection OpenDatabase(
        string databasePath,
        SqliteOpenMode mode,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_session.IsActive)
        {
            throw new OperationCanceledException(_session.CancellationToken);
        }

        SqliteConnection connection = _connectionFactory.Open(
            databasePath,
            _session.DangerousGetMasterKeyMemory(),
            mode);
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

    private static void ValidateOperationId(Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException(
                "Archive rotation operation id cannot be empty.",
                nameof(operationId));
        }
    }
}
