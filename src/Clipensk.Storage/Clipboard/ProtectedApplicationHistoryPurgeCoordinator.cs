using Clipensk.Core.Clipboard;
using Clipensk.Core.Storage;
using Clipensk.Storage.History;
using Clipensk.Storage.Sqlite;

namespace Clipensk.Storage.Clipboard;

/// <summary>What one completed <c>ApplicationHistoryPurge</c> deleted, for the result shown to the user.</summary>
public sealed record ApplicationHistoryPurgeResult(
    Guid OperationId,
    ClipboardHistoryPurgeSummary CurrentSummary,
    ClipboardHistoryPurgeSummary ArchiveSummary,
    int ArchiveDatabaseCount)
{
    public ClipboardHistoryPurgeSummary TotalSummary => CurrentSummary.Add(ArchiveSummary);
}

/// <summary>
/// Runs a confirmed <c>ApplicationHistoryPurge</c> end to end: the Current transaction, then the
/// Archive, Catalog and Trash phases. The caller keeps clipboard capture quiesced throughout. A
/// failure after the Current commit leaves the durable marker for startup recovery, per
/// <c>docs/APPLICATION_GROUP_PROTOCOL.md</c> §9.
/// </summary>
public sealed class ProtectedApplicationHistoryPurgeCoordinator
{
    private readonly ProtectedApplicationHistoryPurgeService _start;
    private readonly ProtectedApplicationHistoryPurgeContinuation _continuation;

    public ProtectedApplicationHistoryPurgeCoordinator(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        _start = new ProtectedApplicationHistoryPurgeService(session, connectionFactory);
        _continuation = new ProtectedApplicationHistoryPurgeContinuation(session, connectionFactory);
    }

    /// <summary>
    /// Gives the previewed root its first personal <paramref name="policy"/> and purges what the
    /// confirmed preview described; external files go to Trash dated <paramref name="deletionDate"/>.
    /// </summary>
    public async Task<ApplicationHistoryPurgeResult> RunConfirmedFirstAssignmentAsync(
        ApplicationHistoryPurgePreview confirmedPreview,
        ClipboardCapturePolicy policy,
        IReadOnlyList<ApplicationCustomBinaryFormatConfiguration>? customBinaryConfigurations,
        DateOnly deletionDate,
        CancellationToken cancellationToken = default)
    {
        ApplicationHistoryPurgeStartResult started = await _start
            .StartConfirmedFirstAssignmentAsync(
                confirmedPreview,
                policy,
                customBinaryConfigurations,
                cancellationToken)
            .ConfigureAwait(false);
        ApplicationHistoryPurgeResumeResult resumed = await _continuation
            .ResumeAsync(deletionDate, cancellationToken)
            .ConfigureAwait(false);
        if (resumed.OperationId != started.OperationId)
        {
            throw new InvalidOperationException(
                "The history purge continuation completed a different operation than the one started.");
        }

        return new ApplicationHistoryPurgeResult(
            started.OperationId,
            started.CurrentSummary,
            resumed.ArchiveSummary,
            resumed.ArchiveDatabaseCount);
    }
}
