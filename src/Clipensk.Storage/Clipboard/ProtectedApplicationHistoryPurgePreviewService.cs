using Clipensk.Core.Applications;
using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Clipensk.Storage.History;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Clipboard;

/// <summary>
/// What moving an application into a user group would do if started now, per
/// <c>docs/APPLICATION_GROUP_PROTOCOL.md</c> §4: the target and source groups and the history the
/// target group's rules would delete. Each logical record is counted once even while it temporarily
/// exists in both Current and an Archive; it is then counted under Current.
/// </summary>
public sealed record ApplicationGroupMovePreview(
    ApplicationId ApplicationId,
    ApplicationGroupId? TargetGroupId,
    ApplicationGroupName TargetGroupName,
    ApplicationGroupId? SourceGroupId,
    bool SourceGroupBecomesEmpty,
    ClipboardHistoryPurgeRule Rule,
    ClipboardHistoryPurgeSummary CurrentSummary,
    ClipboardHistoryPurgeSummary ArchiveSummary,
    int ArchiveDatabaseCount)
{
    public ClipboardHistoryPurgeSummary TotalSummary => CurrentSummary.Add(ArchiveSummary);

    public bool IsEmpty => CurrentSummary.IsEmpty && ArchiveSummary.IsEmpty;

    /// <summary>
    /// The journal filter listing the application's records that hold a representation of
    /// <paramref name="formatName"/>, i.e. the records that lose it — the optional detailed view of
    /// the confirmation.
    /// </summary>
    public ClipboardHistoryFilter RecordsLosing(string formatName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formatName);
        if (Rule.Retains(formatName))
        {
            throw new ArgumentException(
                "The target group keeps this format; no record loses a representation of it.",
                nameof(formatName));
        }

        return new ClipboardHistoryFilter([ApplicationId.Value], formatName);
    }
}

/// <summary>
/// Computes an <see cref="ApplicationGroupMovePreview"/> read-only. Like the unified journal
/// read, it takes no mutation lease — the preview must not stall capture while the user decides —
/// and reads Current before Archives, so a record moving from Current to an Archive meanwhile is
/// still seen. A change of the Archive file set during the read fails it; the caller retries.
/// </summary>
public sealed class ProtectedApplicationHistoryPurgePreviewService
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;

    public ProtectedApplicationHistoryPurgePreviewService(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
    }

    /// <summary>
    /// Previews <paramref name="request"/> with the same preconditions and scope as
    /// <see cref="ProtectedApplicationHistoryPurgeService.StartMoveAsync"/>.
    /// </summary>
    public Task<ApplicationGroupMovePreview> PreviewMoveAsync(
        ApplicationGroupMoveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return PreviewAsync(
            (connection, transaction, token) =>
                ApplicationHistoryPurgeScope.ResolveMoveInTransaction(connection, transaction, request, token),
            cancellationToken);
    }

    private async Task<ApplicationGroupMovePreview> PreviewAsync(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, ApplicationHistoryPurgeScope> resolve,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            cancellationToken);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();

        IReadOnlyList<ArchiveFileName> namesBefore = await Task.Run(
                () => ApplicationGroupMaintenanceDatabase.EnumerateArchives(_session, token),
                CancellationToken.None)
            .ConfigureAwait(false);

        var accumulator = new ClipboardHistoryPurgeSummaryAccumulator();
        (ApplicationHistoryPurgeScope scope, ClipboardHistoryPurgeSummary currentSummary) = await Task.Run(
                () =>
                {
                    using SqliteConnection connection = ApplicationGroupMaintenanceDatabase.OpenCurrent(
                        _session,
                        _connectionFactory,
                        SqliteOpenMode.ReadOnly,
                        token);
                    using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
                    if (SqlitePendingPolicyMaintenanceRepository.ReadInTransaction(
                            connection,
                            transaction,
                            token) is not null)
                    {
                        throw new PendingPolicyMaintenanceException();
                    }

                    ApplicationHistoryPurgeScope resolved = resolve(connection, transaction, token);
                    ClipboardHistoryPurgePlan plan = ClipboardHistoryPurge.PlanInTransaction(
                        connection,
                        transaction,
                        SourceIds(resolved),
                        resolved.Rule,
                        token);
                    return (resolved, accumulator.AddDistinct(plan));
                },
                CancellationToken.None)
            .ConfigureAwait(false);

        var archives = new ProtectedArchiveDatabaseService(_session, _connectionFactory);
        ClipboardHistoryPurgeSummary archiveSummary = ClipboardHistoryPurgeSummary.Empty;
        foreach (ArchiveFileName fileName in namesBefore)
        {
            token.ThrowIfCancellationRequested();
            DatabaseIdentity identity = await archives.ValidateAsync(fileName, token).ConfigureAwait(false);
            ClipboardHistoryPurgeSummary summary = await Task.Run(
                    () =>
                    {
                        using SqliteConnection connection = ApplicationGroupMaintenanceDatabase.OpenArchive(
                            _session,
                            _connectionFactory,
                            fileName,
                            identity.DatabaseId,
                            SqliteOpenMode.ReadOnly,
                            token);
                        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
                        return accumulator.AddDistinct(ClipboardHistoryPurge.PlanInTransaction(
                            connection,
                            transaction,
                            SourceIds(scope),
                            scope.Rule,
                            token));
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            archiveSummary = archiveSummary.Add(summary);
        }

        IReadOnlyList<ArchiveFileName> namesAfter = await Task.Run(
                () => ApplicationGroupMaintenanceDatabase.EnumerateArchives(_session, token),
                CancellationToken.None)
            .ConfigureAwait(false);
        if (!namesBefore.Select(static name => name.FileName)
                .SequenceEqual(namesAfter.Select(static name => name.FileName), StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The Archive directory changed during the history purge preview; retry the preview.");
        }

        token.ThrowIfCancellationRequested();
        return new ApplicationGroupMovePreview(
            scope.ApplicationId,
            scope.TargetGroupId,
            scope.TargetGroupName,
            scope.SourceGroupId,
            scope.SourceGroupBecomesEmpty,
            scope.Rule,
            currentSummary,
            archiveSummary,
            namesBefore.Count);
    }

    private static string[] SourceIds(ApplicationHistoryPurgeScope scope) => [scope.ApplicationId.ToString()];
}
