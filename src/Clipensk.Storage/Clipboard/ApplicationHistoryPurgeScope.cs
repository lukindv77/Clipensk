using System.Globalization;
using Clipensk.Core.Applications;
using Clipensk.Core.Clipboard;
using Clipensk.Storage.Applications;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Clipboard;

/// <summary>
/// What moving an application into a user group would do: the application whose history it purges,
/// the target group and its policy, the fixed purge rule and what happens to the source group. The
/// preview and the start of the operation resolve it by the same code, so a confirmed preview and
/// the move it confirms cannot diverge.
/// </summary>
internal sealed record ApplicationHistoryPurgeScope(
    ApplicationId ApplicationId,
    ApplicationGroupId? TargetGroupId,
    ApplicationGroupName TargetGroupName,
    ClipboardCapturePolicy TargetPolicy,
    ApplicationGroupId? SourceGroupId,
    bool SourceGroupBecomesEmpty,
    ClipboardHistoryPurgeRule Rule)
{
    /// <summary>
    /// Checks the preconditions of <c>docs/APPLICATION_GROUP_PROTOCOL.md</c> §5.4 and resolves the
    /// scope of <paramref name="request"/> against the current groups.
    /// </summary>
    public static ApplicationHistoryPurgeScope ResolveMoveInTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ApplicationGroupMoveRequest request,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ApplicationId);

        if (CapturePolicySql.ReadGlobalPolicyInTransaction(connection, transaction, token) is null)
        {
            throw new InvalidOperationException(
                "Moving an application into a group requires the initial global capture policy.");
        }
        RequireIdentity(connection, transaction, request.ApplicationId);

        ApplicationGroupDirectory directory =
            ApplicationGroupSql.ReadDirectoryInTransaction(connection, transaction, token);
        ApplicationGroup? source = directory.GroupOf(request.ApplicationId);

        ApplicationGroupId? targetGroupId;
        ApplicationGroupName targetName;
        ClipboardCapturePolicy targetPolicy;
        switch (request.Target)
        {
            case NewApplicationGroupTarget created:
                ArgumentNullException.ThrowIfNull(created.Name);
                ApplicationGroup.RequireStandalonePolicy(created.Policy);
                if (directory.IsNameTaken(created.Name))
                {
                    throw new ApplicationGroupNameTakenException();
                }
                targetGroupId = null;
                targetName = created.Name;
                targetPolicy = created.Policy;
                break;
            case ExistingApplicationGroupTarget existing:
                ArgumentNullException.ThrowIfNull(existing.GroupId);
                ApplicationGroup group = directory.FindGroup(existing.GroupId)
                    ?? throw new InvalidOperationException("The target application group does not exist.");
                if (source?.GroupId == group.GroupId)
                {
                    throw new InvalidOperationException("The application is already a member of the target group.");
                }
                targetGroupId = group.GroupId;
                targetName = group.Name;
                targetPolicy = group.Policy;
                break;
            default:
                throw new ArgumentException("Unknown application group move target.", nameof(request));
        }

        bool sourceBecomesEmpty = source is not null && directory.MembersOf(source.GroupId).Count == 1;
        if (request.DeleteEmptiedSourceGroup && !sourceBecomesEmpty)
        {
            throw new InvalidOperationException(
                "Only a user group left without applications by the move can be deleted with it.");
        }

        return new ApplicationHistoryPurgeScope(
            request.ApplicationId,
            targetGroupId,
            targetName,
            targetPolicy,
            source?.GroupId,
            sourceBecomesEmpty,
            ClipboardHistoryPurgeRule.FromEffectivePolicy(targetPolicy));
    }

    /// <summary>Whether this move does exactly what <paramref name="preview"/> described.</summary>
    public bool Matches(ApplicationGroupMovePreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return ApplicationId == preview.ApplicationId &&
            TargetGroupId == preview.TargetGroupId &&
            string.Equals(TargetGroupName.Key, preview.TargetGroupName.Key, StringComparison.Ordinal) &&
            SourceGroupId == preview.SourceGroupId &&
            SourceGroupBecomesEmpty == preview.SourceGroupBecomesEmpty &&
            Rule.Equals(preview.Rule);
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
            throw new InvalidOperationException("Moving an application requires an existing application identity.");
        }
    }
}
