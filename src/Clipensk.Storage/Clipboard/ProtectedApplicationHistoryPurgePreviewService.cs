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
/// What an <c>ApplicationHistoryPurge</c> would delete if started now, per
/// <c>docs/APPLICATION_GROUP_PROTOCOL.md</c> §4. Each logical record is counted once even while it
/// temporarily exists in both Current and an Archive; it is then counted under Current.
/// </summary>
public sealed record ApplicationHistoryPurgePreview(
    ApplicationId RootApplicationId,
    IReadOnlyList<ApplicationId> SourceApplicationIds,
    ClipboardHistoryPurgeRule Rule,
    ClipboardHistoryPurgeSummary CurrentSummary,
    ClipboardHistoryPurgeSummary ArchiveSummary,
    int ArchiveDatabaseCount)
{
    public ClipboardHistoryPurgeSummary TotalSummary => CurrentSummary.Add(ArchiveSummary);

    public bool IsEmpty => CurrentSummary.IsEmpty && ArchiveSummary.IsEmpty;

    /// <summary>
    /// The journal filter listing the records in scope that hold a representation of
    /// <paramref name="formatName"/>, i.e. the records that lose it — the optional detailed view of
    /// the confirmation.
    /// </summary>
    public ClipboardHistoryFilter RecordsLosing(string formatName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formatName);
        if (Rule.Retains(formatName))
        {
            throw new ArgumentException(
                "The purge keeps this format; no record loses a representation of it.",
                nameof(formatName));
        }

        return new ClipboardHistoryFilter(SourceApplicationIds.Select(static id => id.Value), formatName);
    }
}

/// <summary>
/// Computes an <see cref="ApplicationHistoryPurgePreview"/> read-only. Like the unified journal
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
    /// Previews giving an unconfigured group root its first personal policy, with the same
    /// preconditions and scope as
    /// <see cref="ProtectedApplicationHistoryPurgeService.StartFirstAssignmentAsync"/>.
    /// </summary>
    public Task<ApplicationHistoryPurgePreview> PreviewFirstAssignmentAsync(
        ApplicationId rootApplicationId,
        ClipboardCapturePolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rootApplicationId);
        CapturePolicySql.ValidateApplicationPolicy(policy);
        return PreviewAsync(
            (connection, transaction, token) => ApplicationHistoryPurgeScope.ResolveFirstAssignmentInTransaction(
                connection,
                transaction,
                rootApplicationId,
                policy,
                token),
            cancellationToken);
    }

    private async Task<ApplicationHistoryPurgePreview> PreviewAsync(
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
        return new ApplicationHistoryPurgePreview(
            scope.RootApplicationId,
            scope.SourceApplicationIds,
            scope.Rule,
            currentSummary,
            archiveSummary,
            namesBefore.Count);
    }

    private static string[] SourceIds(ApplicationHistoryPurgeScope scope) =>
        scope.SourceApplicationIds.Select(static id => id.ToString()).ToArray();
}
