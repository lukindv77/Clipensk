using System.Globalization;
using Clipensk.Core.Applications;
using Clipensk.Core.Clipboard;
using Clipensk.Storage.Applications;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Clipboard;

/// <summary>
/// What one <c>ApplicationHistoryPurge</c> would purge: the source applications whose history it
/// covers and the fixed rule it purges by. The preview and the start of an operation resolve it by
/// the same code, so a confirmed preview and the purge it confirms cannot diverge in scope.
/// </summary>
internal sealed record ApplicationHistoryPurgeScope(
    ApplicationId RootApplicationId,
    IReadOnlyList<ApplicationId> SourceApplicationIds,
    ClipboardHistoryPurgeRule Rule)
{
    /// <summary>
    /// Checks the preconditions of <c>docs/APPLICATION_GROUP_PROTOCOL.md</c> §5.4 — the global
    /// policy exists; the root is an existing identity, not a group member and not yet personally
    /// configured — and resolves the scope of giving the root <paramref name="policy"/>: the root's
    /// whole group, purged by <c>Merge(global, policy)</c>.
    /// </summary>
    public static ApplicationHistoryPurgeScope ResolveFirstAssignmentInTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ApplicationId rootApplicationId,
        ClipboardCapturePolicy policy,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(rootApplicationId);
        ArgumentNullException.ThrowIfNull(policy);

        ClipboardCapturePolicy global =
            CapturePolicySql.ReadGlobalPolicyInTransaction(connection, transaction, token)
            ?? throw new InvalidOperationException(
                "A history purge requires the initial global capture policy to be saved first.");
        ApplicationGroupSnapshot groups =
            SqliteApplicationGroupRepository.ReadSnapshotInTransaction(connection, transaction, token);
        RequireIdentity(connection, transaction, rootApplicationId);
        if (groups.IsMember(rootApplicationId))
        {
            throw new InvalidOperationException(
                "A group member has no policy of its own; assign the group root's policy instead.");
        }
        if (groups.IsPersonallyConfigured(rootApplicationId))
        {
            throw new InvalidOperationException(
                "The group root already has a personal policy; an edit publishes without a purge.");
        }

        return new ApplicationHistoryPurgeScope(
            rootApplicationId,
            groups.MembersOf(rootApplicationId),
            ClipboardHistoryPurgeRule.FromEffectivePolicy(
                new ClipboardCapturePolicyEvaluator().Merge(global, policy)));
    }

    private static void RequireIdentity(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ApplicationId applicationId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM ApplicationIdentity
            WHERE ApplicationId = $applicationId COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$applicationId", applicationId.ToString());
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
        {
            throw new InvalidOperationException("A history purge requires an existing application identity.");
        }
    }
}
