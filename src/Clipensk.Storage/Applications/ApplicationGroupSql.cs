using System.Globalization;
using Clipensk.Core.Applications;
using Clipensk.Core.Clipboard;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Applications;

/// <summary>
/// Reads and writes Current v12 application groups inside a caller-owned transaction, per
/// <c>docs/APPLICATION_GROUP_PROTOCOL.md</c> §2. Persisted data that breaks a group invariant fails
/// closed instead of being interpreted. Writers are used only by operations that hold the storage
/// mutation lease.
/// </summary>
internal static class ApplicationGroupSql
{
    public static ApplicationGroupDirectory ReadDirectoryInTransaction(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(connection);
        token.ThrowIfCancellationRequested();

        Dictionary<string, Dictionary<string, ClipboardFormatCapturePolicy>> formats =
            ReadFormatRules(connection, transaction, groupId: null, token);
        var groups = new List<ApplicationGroup>();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT GroupId, Name, NameKey, CaptureRule, CreatedAtUtc
                FROM ApplicationGroup
                ORDER BY GroupId COLLATE BINARY;
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                string groupId = reader.GetString(0);
                groups.Add(ReadGroup(reader, formats.GetValueOrDefault(groupId)));
                formats.Remove(groupId);
            }
        }

        if (formats.Count != 0)
        {
            throw new InvalidDataException("A group format rule refers to an unknown application group.");
        }

        var assignments = new List<ApplicationGroupAssignment>();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT ApplicationId, GroupId, JoinedAtUtc
                FROM ApplicationGroupMembership
                ORDER BY ApplicationId COLLATE BINARY;
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                assignments.Add(new ApplicationGroupAssignment(
                    new ApplicationId(ParseCanonicalGuid(reader.GetString(0))),
                    new ApplicationGroupId(ParseCanonicalGuid(reader.GetString(1))),
                    ParseUtc(reader.GetString(2))));
            }
        }

        token.ThrowIfCancellationRequested();
        return new ApplicationGroupDirectory(groups, assignments);
    }

    /// <summary>The application's user group, or <see langword="null"/> for the default group.</summary>
    public static ApplicationGroup? ReadGroupOfInTransaction(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ApplicationId applicationId,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(applicationId);
        token.ThrowIfCancellationRequested();

        string? groupId;
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT GroupId
                FROM ApplicationGroupMembership
                WHERE ApplicationId = $applicationId COLLATE BINARY;
                """;
            command.Parameters.AddWithValue("$applicationId", applicationId.ToString());
            groupId = command.ExecuteScalar() as string;
        }

        return groupId is null
            ? null
            : ReadGroupInTransaction(connection, transaction, new ApplicationGroupId(ParseCanonicalGuid(groupId)), token)
              ?? throw new InvalidDataException("An application belongs to a missing group.");
    }

    public static ApplicationGroup? ReadGroupInTransaction(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ApplicationGroupId groupId,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(groupId);
        Dictionary<string, Dictionary<string, ClipboardFormatCapturePolicy>> formats =
            ReadFormatRules(connection, transaction, groupId, token);

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT GroupId, Name, NameKey, CaptureRule, CreatedAtUtc
            FROM ApplicationGroup
            WHERE GroupId = $groupId COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$groupId", groupId.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            if (formats.Count != 0)
            {
                throw new InvalidDataException("A group format rule refers to an unknown application group.");
            }
            return null;
        }

        return ReadGroup(reader, formats.GetValueOrDefault(groupId.ToString()));
    }

    public static void InsertGroupInTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ApplicationGroup group,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(group);
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO ApplicationGroup (GroupId, Name, NameKey, CaptureRule, CreatedAtUtc)
                VALUES ($groupId, $name, $nameKey, $captureRule, $createdAtUtc);
                """;
            command.Parameters.AddWithValue("$groupId", group.GroupId.ToString());
            command.Parameters.AddWithValue("$name", group.Name.Value);
            command.Parameters.AddWithValue("$nameKey", group.Name.Key);
            command.Parameters.AddWithValue("$captureRule", group.Policy.Capture.ToString());
            command.Parameters.AddWithValue("$createdAtUtc", FormatUtc(group.CreatedAtUtc));
            command.ExecuteNonQuery();
        }

        InsertFormatRules(connection, transaction, group.GroupId, group.Policy, token);
    }

    public static void ReplaceGroupPolicyInTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ApplicationGroupId groupId,
        ClipboardCapturePolicy policy,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(groupId);
        ApplicationGroup.RequireStandalonePolicy(policy);
        using (SqliteCommand update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE ApplicationGroup
                SET CaptureRule = $captureRule
                WHERE GroupId = $groupId COLLATE BINARY;
                """;
            update.Parameters.AddWithValue("$groupId", groupId.ToString());
            update.Parameters.AddWithValue("$captureRule", policy.Capture.ToString());
            if (update.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException("The application group does not exist.");
            }
        }

        DeleteFormatRules(connection, transaction, groupId);
        InsertFormatRules(connection, transaction, groupId, policy, token);
    }

    public static void RenameGroupInTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ApplicationGroupId groupId,
        ApplicationGroupName name)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(groupId);
        ArgumentNullException.ThrowIfNull(name);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE ApplicationGroup
            SET Name = $name, NameKey = $nameKey
            WHERE GroupId = $groupId COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$groupId", groupId.ToString());
        command.Parameters.AddWithValue("$name", name.Value);
        command.Parameters.AddWithValue("$nameKey", name.Key);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("The application group does not exist.");
        }
    }

    /// <summary>Deletes a group that has no members, together with its policy.</summary>
    public static void DeleteEmptyGroupInTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ApplicationGroupId groupId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(groupId);
        using (SqliteCommand members = connection.CreateCommand())
        {
            members.Transaction = transaction;
            members.CommandText = """
                SELECT COUNT(*)
                FROM ApplicationGroupMembership
                WHERE GroupId = $groupId COLLATE BINARY;
                """;
            members.Parameters.AddWithValue("$groupId", groupId.ToString());
            if (Convert.ToInt64(members.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
            {
                throw new InvalidOperationException("An application group with members cannot be deleted.");
            }
        }

        DeleteFormatRules(connection, transaction, groupId);
        using SqliteCommand delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM ApplicationGroup WHERE GroupId = $groupId COLLATE BINARY;";
        delete.Parameters.AddWithValue("$groupId", groupId.ToString());
        if (delete.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("The application group does not exist.");
        }
    }

    /// <summary>Makes the application a member of the group, replacing any previous membership.</summary>
    public static void SetMembershipInTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ApplicationId applicationId,
        ApplicationGroupId groupId,
        DateTimeOffset joinedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(applicationId);
        ArgumentNullException.ThrowIfNull(groupId);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ApplicationGroupMembership (ApplicationId, GroupId, JoinedAtUtc)
            VALUES ($applicationId, $groupId, $joinedAtUtc)
            ON CONFLICT (ApplicationId) DO UPDATE
            SET GroupId = excluded.GroupId, JoinedAtUtc = excluded.JoinedAtUtc;
            """;
        command.Parameters.AddWithValue("$applicationId", applicationId.ToString());
        command.Parameters.AddWithValue("$groupId", groupId.ToString());
        command.Parameters.AddWithValue("$joinedAtUtc", FormatUtc(joinedAtUtc));
        command.ExecuteNonQuery();
    }

    public static string FormatUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Application group timestamps must be UTC.", nameof(value));
        }
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    private static ApplicationGroup ReadGroup(
        SqliteDataReader reader,
        Dictionary<string, ClipboardFormatCapturePolicy>? formats)
    {
        Guid groupId = ParseCanonicalGuid(reader.GetString(0));
        string storedName = reader.GetString(1);
        if (!ApplicationGroupName.TryCreate(storedName, out ApplicationGroupName? name, out _) ||
            !string.Equals(name!.Value, storedName, StringComparison.Ordinal) ||
            !string.Equals(name.Key, reader.GetString(2), StringComparison.Ordinal))
        {
            throw new InvalidDataException("An application group has an invalid stored name.");
        }

        return new ApplicationGroup(
            new ApplicationGroupId(groupId),
            name,
            new ClipboardCapturePolicy(ParseRule(reader.GetString(3)), formats),
            ParseUtc(reader.GetString(4)));
    }

    private static Dictionary<string, Dictionary<string, ClipboardFormatCapturePolicy>> ReadFormatRules(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ApplicationGroupId? groupId,
        CancellationToken token)
    {
        var result = new Dictionary<string, Dictionary<string, ClipboardFormatCapturePolicy>>(StringComparer.Ordinal);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT GroupId, FormatName, CaptureRule, MaxBytes
            FROM ApplicationGroupFormatCapturePolicy
            WHERE $groupId IS NULL OR GroupId = $groupId COLLATE BINARY
            ORDER BY GroupId COLLATE BINARY, FormatName COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$groupId", (object?)groupId?.ToString() ?? DBNull.Value);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            string owner = ParseCanonicalGuid(reader.GetString(0)).ToString("D");
            string formatName = reader.GetString(1);
            long? maxBytes = reader.IsDBNull(3) ? null : reader.GetInt64(3);
            if (string.IsNullOrWhiteSpace(formatName) || maxBytes is <= 0)
            {
                throw new InvalidDataException("An application group has an invalid format rule.");
            }
            if (!result.TryGetValue(owner, out Dictionary<string, ClipboardFormatCapturePolicy>? rules))
            {
                rules = new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal);
                result.Add(owner, rules);
            }
            rules.Add(formatName, new ClipboardFormatCapturePolicy(ParseRule(reader.GetString(2)), maxBytes));
        }

        return result;
    }

    private static void InsertFormatRules(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ApplicationGroupId groupId,
        ClipboardCapturePolicy policy,
        CancellationToken token)
    {
        ApplicationGroup.RequireStandalonePolicy(policy);
        foreach ((string formatName, ClipboardFormatCapturePolicy format) in policy.Formats
                     .OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO ApplicationGroupFormatCapturePolicy (GroupId, FormatName, CaptureRule, MaxBytes)
                VALUES ($groupId, $formatName, $captureRule, $maxBytes);
                """;
            command.Parameters.AddWithValue("$groupId", groupId.ToString());
            command.Parameters.AddWithValue("$formatName", formatName);
            command.Parameters.AddWithValue("$captureRule", format.Capture.ToString());
            command.Parameters.AddWithValue("$maxBytes", (object?)format.MaxBytes ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    private static void DeleteFormatRules(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ApplicationGroupId groupId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM ApplicationGroupFormatCapturePolicy
            WHERE GroupId = $groupId COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$groupId", groupId.ToString());
        command.ExecuteNonQuery();
    }

    private static ClipboardCapturePolicyRule ParseRule(string value) => value switch
    {
        "Allow" => ClipboardCapturePolicyRule.Allow,
        "Deny" => ClipboardCapturePolicyRule.Deny,
        _ => throw new InvalidDataException("An application group has an invalid capture rule."),
    };

    private static Guid ParseCanonicalGuid(string value)
    {
        if (!Guid.TryParseExact(value, "D", out Guid parsed) ||
            parsed == Guid.Empty ||
            !string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal))
        {
            throw new InvalidDataException("Application group data contains a non-canonical identifier.");
        }
        return parsed;
    }

    private static DateTimeOffset ParseUtc(string value)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTimeOffset parsed) ||
            parsed.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("Application group data contains an invalid UTC timestamp.");
        }
        return parsed;
    }
}
