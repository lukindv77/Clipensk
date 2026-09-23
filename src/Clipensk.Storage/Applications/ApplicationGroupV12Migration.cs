using Clipensk.Core.Applications;
using Clipensk.Core.Clipboard;
using Clipensk.Storage.Clipboard;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Applications;

/// <summary>
/// The Current v11 → v12 data step of <c>docs/APPLICATION_GROUP_PROTOCOL.md</c> §2.2, run inside
/// the migration transaction: every application with a legacy personal policy becomes the only
/// member of a group named after it, whose standalone policy is exactly the policy capture applied
/// to it before; v11 group memberships follow their root; the v11 membership table is dropped. The
/// legacy per-application policy tables stay, read only by a pending legacy maintenance operation.
/// </summary>
internal static class ApplicationGroupV12Migration
{
    public static void MigrateInTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset migratedAtUtc,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ApplicationGroupSqlSchema.CreateTables(connection, transaction);

        List<ApplicationId> configured = ReadIds(
            connection,
            transaction,
            "SELECT ApplicationId FROM ApplicationCapturePolicy ORDER BY ApplicationId COLLATE BINARY;");
        var groupOf = new Dictionary<ApplicationId, ApplicationGroupId>();
        if (configured.Count > 0)
        {
            // Stores migrated from before the global policy existed can hold personal policies with
            // no global one: nothing is captured until it is set up. Its parts are unknown, so only
            // the explicit personal rules survive and everything inherited becomes Deny — the group
            // never captures more than the user explicitly allowed.
            ClipboardCapturePolicy global =
                CapturePolicySql.ReadGlobalPolicyInTransaction(connection, transaction, token)
                ?? new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Deny);
            Dictionary<ApplicationId, ApplicationIdentitySummary> identities = SqliteApplicationIdentityRepository
                .List(connection, token, transaction)
                .ToDictionary(static summary => summary.ApplicationId);
            var evaluator = new ClipboardCapturePolicyEvaluator();
            var takenKeys = new HashSet<string>(StringComparer.Ordinal);

            foreach (ApplicationId applicationId in configured)
            {
                token.ThrowIfCancellationRequested();
                ClipboardCapturePolicy personal =
                    CapturePolicySql.ReadApplicationPolicyInTransaction(connection, transaction, applicationId, token)
                    ?? throw new InvalidDataException("A personal capture policy disappeared during migration.");
                if (!identities.TryGetValue(applicationId, out ApplicationIdentitySummary? identity))
                {
                    throw new InvalidDataException("A personal capture policy belongs to an unknown application.");
                }

                ApplicationGroupName name = ApplicationGroupName.CreateUniqueFromApplicationName(
                    ApplicationDisplayName.From(identity),
                    takenKeys.Contains);
                takenKeys.Add(name.Key);
                var group = new ApplicationGroup(
                    ApplicationGroupId.New(),
                    name,
                    ToStandalone(evaluator.Merge(global, personal)),
                    migratedAtUtc);
                ApplicationGroupSql.InsertGroupInTransaction(connection, transaction, group, token);
                ApplicationGroupSql.SetMembershipInTransaction(
                    connection,
                    transaction,
                    applicationId,
                    group.GroupId,
                    migratedAtUtc);
                groupOf.Add(applicationId, group.GroupId);
            }
        }

        using (SqliteCommand members = connection.CreateCommand())
        {
            members.Transaction = transaction;
            members.CommandText = """
                SELECT ApplicationId, ParentApplicationId
                FROM ApplicationGroupMember
                ORDER BY ApplicationId COLLATE BINARY;
                """;
            var joins = new List<(ApplicationId Member, ApplicationGroupId Group)>();
            using (SqliteDataReader reader = members.ExecuteReader())
            {
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    var member = new ApplicationId(ParseCanonicalGuid(reader.GetString(0)));
                    var root = new ApplicationId(ParseCanonicalGuid(reader.GetString(1)));
                    if (groupOf.ContainsKey(member))
                    {
                        throw new InvalidDataException("A v11 group member owns a personal capture policy.");
                    }
                    if (groupOf.TryGetValue(root, out ApplicationGroupId? groupId))
                    {
                        joins.Add((member, groupId));
                    }
                }
            }

            foreach ((ApplicationId member, ApplicationGroupId groupId) in joins)
            {
                ApplicationGroupSql.SetMembershipInTransaction(connection, transaction, member, groupId, migratedAtUtc);
            }
        }

        using SqliteCommand drop = connection.CreateCommand();
        drop.Transaction = transaction;
        drop.CommandText = $"""
            DROP INDEX {ApplicationGroupMemberSqlSchema.IndexName};
            DROP TABLE ApplicationGroupMember;
            """;
        drop.ExecuteNonQuery();
        token.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// A merged policy made standalone without changing capture: capture treats anything but
    /// Allow as not allowed, so a rule still Inherit after the merge becomes Deny.
    /// </summary>
    internal static ClipboardCapturePolicy ToStandalone(ClipboardCapturePolicy effective) =>
        new(
            Explicit(effective.Capture),
            effective.Formats.ToDictionary(
                static pair => pair.Key,
                static pair => new ClipboardFormatCapturePolicy(Explicit(pair.Value.Capture), pair.Value.MaxBytes),
                StringComparer.Ordinal));

    private static ClipboardCapturePolicyRule Explicit(ClipboardCapturePolicyRule rule) =>
        rule == ClipboardCapturePolicyRule.Allow ? ClipboardCapturePolicyRule.Allow : ClipboardCapturePolicyRule.Deny;

    private static List<ApplicationId> ReadIds(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var result = new List<ApplicationId>();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ApplicationId(ParseCanonicalGuid(reader.GetString(0))));
        }
        return result;
    }

    private static Guid ParseCanonicalGuid(string value)
    {
        if (!Guid.TryParseExact(value, "D", out Guid parsed) ||
            parsed == Guid.Empty ||
            !string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal))
        {
            throw new InvalidDataException("Current contains a non-canonical ApplicationId.");
        }
        return parsed;
    }
}
