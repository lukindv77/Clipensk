using Clipensk.Core.Applications;
using Clipensk.Core.Storage;
using Clipensk.Storage.History;
using Clipensk.Storage.Sqlite;

namespace Clipensk.Storage.Clipboard;

/// <summary>What one completed move deleted, for the result shown to the user.</summary>
public sealed record ApplicationHistoryPurgeResult(
    Guid OperationId,
    ApplicationGroupId GroupId,
    ClipboardHistoryPurgeSummary CurrentSummary,
    ClipboardHistoryPurgeSummary ArchiveSummary,
    int ArchiveDatabaseCount)
{
    public ClipboardHistoryPurgeSummary TotalSummary => CurrentSummary.Add(ArchiveSummary);
}

/// <summary>
/// A confirmed move committed its Current phase, but a later phase failed. The move itself and the
/// Current purge are durable; the marker stays and startup recovery finishes the Archive, Catalog
/// and Trash phases (<c>docs/APPLICATION_GROUP_PROTOCOL.md</c> §9).
/// </summary>
public sealed class ApplicationHistoryPurgeIncompleteException : Exception
{
    public ApplicationHistoryPurgeIncompleteException(
        Guid operationId,
        ApplicationGroupId groupId,
        ClipboardHistoryPurgeSummary currentSummary,
        Exception innerException)
        : base("The application moved, but purging its archived history is not finished yet.", innerException)
    {
        OperationId = operationId;
        GroupId = groupId;
        CurrentSummary = currentSummary;
    }

    public Guid OperationId { get; }

    public ApplicationGroupId GroupId { get; }

    public ClipboardHistoryPurgeSummary CurrentSummary { get; }
}

/// <summary>
/// Runs a confirmed move end to end: the Current transaction, then the Archive, Catalog and Trash
/// phases. The caller keeps clipboard capture quiesced throughout.
/// </summary>
public sealed class ProtectedApplicationHistoryPurgeCoordinator
{
    private readonly ProtectedApplicationHistoryPurgeService _start;
    private readonly ProtectedApplicationHistoryPurgeContinuation _continuation;

    public ProtectedApplicationHistoryPurgeCoordinator(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
        : this(session, connectionFactory, continuationCheckpoint: null)
    {
    }

    internal ProtectedApplicationHistoryPurgeCoordinator(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory,
        Action<ApplicationHistoryPurgeContinuationCheckpoint>? continuationCheckpoint)
    {
        ArgumentNullException.ThrowIfNull(session);
        _start = new ProtectedApplicationHistoryPurgeService(session, connectionFactory);
        _continuation = new ProtectedApplicationHistoryPurgeContinuation(
            session,
            connectionFactory,
            continuationCheckpoint);
    }

    /// <summary>
    /// Performs the move the user confirmed from <paramref name="confirmedPreview"/>; external files
    /// go to Trash dated <paramref name="deletionDate"/>.
    /// </summary>
    public async Task<ApplicationHistoryPurgeResult> RunConfirmedMoveAsync(
        ApplicationGroupMoveRequest request,
        ApplicationGroupMovePreview confirmedPreview,
        DateOnly deletionDate,
        CancellationToken cancellationToken = default)
    {
        ApplicationHistoryPurgeStartResult started = await _start
            .StartConfirmedMoveAsync(request, confirmedPreview, cancellationToken)
            .ConfigureAwait(false);

        ApplicationHistoryPurgeResumeResult resumed;
        try
        {
            resumed = await _continuation
                .ResumeAsync(deletionDate, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ApplicationHistoryPurgeIncompleteException(
                started.OperationId,
                started.GroupId,
                started.CurrentSummary,
                exception);
        }

        if (resumed.OperationId != started.OperationId)
        {
            throw new InvalidOperationException(
                "The history purge continuation completed a different operation than the one started.");
        }

        return new ApplicationHistoryPurgeResult(
            started.OperationId,
            started.GroupId,
            started.CurrentSummary,
            resumed.ArchiveSummary,
            resumed.ArchiveDatabaseCount);
    }
}
