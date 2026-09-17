using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Sqlite;

namespace Clipensk.Storage.Databases;

/// <summary>
/// User-facing Archive Split orchestration over the durable phase-specific services.
/// Every phase remains independently recoverable from PendingArchiveSplit.
/// </summary>
public sealed class ProtectedArchiveSplitCoordinator
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly SqlitePendingArchiveSplitRepository _pendingRepository;
    private readonly ProtectedArchiveSegmentCatalog _archiveCatalog;
    private readonly ProtectedArchiveDatabaseService _archiveService;
    private readonly ProtectedArchiveSplitShadowBuilder _shadowBuilder;
    private readonly ProtectedArchiveSplitPublisher _physicalPublisher;
    private readonly ProtectedArchiveSplitCatalogPublisher _catalogPublisher;

    public ProtectedArchiveSplitCoordinator(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
        _pendingRepository = new SqlitePendingArchiveSplitRepository(session, _connectionFactory);
        _archiveCatalog = new ProtectedArchiveSegmentCatalog(session, _connectionFactory);
        _archiveService = new ProtectedArchiveDatabaseService(session, _connectionFactory);
        _shadowBuilder = new ProtectedArchiveSplitShadowBuilder(session, _connectionFactory);
        _physicalPublisher = new ProtectedArchiveSplitPublisher(session, _connectionFactory);
        _catalogPublisher = new ProtectedArchiveSplitCatalogPublisher(session, _connectionFactory);
    }

    public async Task<IReadOnlyList<ArchiveSegmentDescriptor>> SplitAsync(
        ArchiveFileName sourceFileName,
        IReadOnlyCollection<JournalDateRange> requestedResultRanges,
        DateOnly currentCalendarDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestedResultRanges);
        cancellationToken.ThrowIfCancellationRequested();

        if (await _pendingRepository.ReadAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new InvalidOperationException(
                "A pending Archive Split must be recovered before a new split can start.");
        }

        IReadOnlyList<ArchiveSegmentDescriptor> descriptors =
            await _archiveCatalog.ValidateConsistencyAsync(
                currentCalendarDate,
                cancellationToken).ConfigureAwait(false);

        ArchiveSegmentDescriptor sourceDescriptor = descriptors.SingleOrDefault(
            item => string.Equals(
                item.FileName,
                sourceFileName.FileName,
                StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "The selected Archive is not present in the validated storage catalog.");

        DatabaseIdentity sourceIdentity = await _archiveService
            .ValidateAsync(sourceFileName, cancellationToken)
            .ConfigureAwait(false);
        JournalDateRange sourceCoverage = GetCoverage(sourceIdentity);
        if (sourceIdentity.DatabaseId != sourceDescriptor.DatabaseId ||
            sourceCoverage != sourceDescriptor.Coverage)
        {
            throw new InvalidDataException(
                "The selected Archive identity/coverage does not match the validated storage catalog.");
        }

        ArchiveFileName[] occupiedFamily = descriptors
            .Select(item => ParseCanonicalFileName(item.FileName))
            .Where(item => item.BaseNumber == sourceFileName.BaseNumber)
            .OrderBy(item => item.SplitSequence)
            .ToArray();

        IReadOnlyList<PendingArchiveSplitSegment> plan = new ArchiveSplitPlanner().Build(
            sourceFileName,
            sourceIdentity.DatabaseId,
            sourceCoverage,
            requestedResultRanges,
            occupiedFamily,
            Guid.NewGuid);

        PendingArchiveSplitOperation operation = await _pendingRepository.StartAsync(
            sourceFileName,
            sourceIdentity.DatabaseId,
            sourceCoverage,
            plan,
            cancellationToken).ConfigureAwait(false);

        return await ResumeAsync(
            operation.OperationId,
            currentCalendarDate,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ArchiveSegmentDescriptor>> ResumeAsync(
        Guid operationId,
        DateOnly currentCalendarDate,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException(
                "Archive split operation id cannot be empty.",
                nameof(operationId));
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PendingArchiveSplitOperation operation =
                await _pendingRepository.ReadAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("No pending Archive Split exists.");
            if (operation.OperationId != operationId)
            {
                throw new InvalidOperationException(
                    "Pending Archive Split ownership does not match the requested operation.");
            }

            switch (operation.Phase)
            {
                case ArchiveSplitPhase.Planned:
                    _ = await _shadowBuilder.BuildAsync(
                        operationId,
                        cancellationToken).ConfigureAwait(false);
                    break;

                case ArchiveSplitPhase.ReadyToPublish:
                    _ = await _physicalPublisher.PublishOrRecoverAsync(
                        operationId,
                        cancellationToken).ConfigureAwait(false);
                    break;

                case ArchiveSplitPhase.PhysicalPublished:
                case ArchiveSplitPhase.CatalogPublished:
                    return await _catalogPublisher.PublishOrRecoverAsync(
                        operationId,
                        currentCalendarDate,
                        cancellationToken).ConfigureAwait(false);

                default:
                    throw new InvalidDataException(
                        "Pending Archive Split has an unsupported durable phase.");
            }
        }
    }

    public ValueTask<PendingArchiveSplitOperation?> ReadPendingAsync(
        CancellationToken cancellationToken = default) =>
        _pendingRepository.ReadAsync(cancellationToken);

    private static ArchiveFileName ParseCanonicalFileName(string fileName)
    {
        if (!ArchiveFileName.TryParse(fileName, out ArchiveFileName parsed) ||
            !string.Equals(fileName, parsed.FileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Storage catalog contains noncanonical Archive filename '{fileName}'.");
        }

        return parsed;
    }

    private static JournalDateRange GetCoverage(DatabaseIdentity identity)
    {
        if (identity.CoverageStartDate is not DateOnly start ||
            identity.CoverageEndDate is not DateOnly end ||
            end < start)
        {
            throw new InvalidDataException("Archive coverage is missing or invalid.");
        }

        return new JournalDateRange(start, end);
    }
}
