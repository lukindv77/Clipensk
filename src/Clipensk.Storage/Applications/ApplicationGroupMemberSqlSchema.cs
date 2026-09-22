using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Applications;

/// <summary>
/// Current v11 group membership, per <c>docs/APPLICATION_GROUP_PROTOCOL.md</c> §2.1. It is a
/// separate table rather than a column of <c>ApplicationIdentity</c> because that schema is shared
/// with Archive v1, which would otherwise need a migration.
/// </summary>
internal static class ApplicationGroupMemberSqlSchema
{
    public const int MinimumCurrentSchemaVersion = 11;
    public const string IndexName = "IX_ApplicationGroupMember_ParentApplicationId";

    public static void CreateTable(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            CREATE TABLE ApplicationGroupMember (
                ApplicationId TEXT NOT NULL PRIMARY KEY,
                ParentApplicationId TEXT NOT NULL,
                RetainedFromApplicationId TEXT NULL,
                JoinedAtUtc TEXT NOT NULL,
                CHECK (ApplicationId <> ParentApplicationId),
                CHECK (RetainedFromApplicationId IS NULL OR RetainedFromApplicationId <> ApplicationId),
                FOREIGN KEY (ApplicationId)
                    REFERENCES ApplicationIdentity(ApplicationId),
                FOREIGN KEY (ParentApplicationId)
                    REFERENCES ApplicationIdentity(ApplicationId),
                FOREIGN KEY (RetainedFromApplicationId)
                    REFERENCES ApplicationIdentity(ApplicationId)
            );

            CREATE INDEX {IndexName}
                ON ApplicationGroupMember(ParentApplicationId);
            """;
        command.ExecuteNonQuery();
    }

    public static void ValidateTable(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info('ApplicationGroupMember');";
            using SqliteDataReader reader = command.ExecuteReader();
            ExpectedColumn[] expected =
            [
                new("ApplicationId", "TEXT", NotNull: true, PrimaryKeyOrder: 1),
                new("ParentApplicationId", "TEXT", NotNull: true, PrimaryKeyOrder: 0),
                new("RetainedFromApplicationId", "TEXT", NotNull: false, PrimaryKeyOrder: 0),
                new("JoinedAtUtc", "TEXT", NotNull: true, PrimaryKeyOrder: 0),
            ];

            int index = 0;
            while (reader.Read())
            {
                if (index >= expected.Length)
                {
                    throw new InvalidDataException(
                        "ApplicationGroupMember contains unexpected columns.");
                }

                ExpectedColumn column = expected[index++];
                if (!string.Equals(reader.GetString(1), column.Name, StringComparison.Ordinal) ||
                    !string.Equals(reader.GetString(2), column.Type, StringComparison.OrdinalIgnoreCase) ||
                    reader.GetInt32(3) != (column.NotNull ? 1 : 0) ||
                    reader.GetInt32(5) != column.PrimaryKeyOrder)
                {
                    throw new InvalidDataException(
                        "ApplicationGroupMember column contract is invalid.");
                }
            }

            if (index != expected.Length)
            {
                throw new InvalidDataException(
                    "ApplicationGroupMember is missing required columns.");
            }
        }

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_key_list('ApplicationGroupMember');";
            using SqliteDataReader reader = command.ExecuteReader();
            var fromColumns = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read())
            {
                // Identities are never deleted; a delete that would orphan a membership must fail
                // rather than silently reshape a group, so no ON DELETE action is allowed.
                if (!string.Equals(reader.GetString(2), "ApplicationIdentity", StringComparison.Ordinal) ||
                    !string.Equals(reader.GetString(4), "ApplicationId", StringComparison.Ordinal) ||
                    !string.Equals(reader.GetString(6), "NO ACTION", StringComparison.OrdinalIgnoreCase) ||
                    !fromColumns.Add(reader.GetString(3)))
                {
                    throw new InvalidDataException(
                        "ApplicationGroupMember foreign-key contract is invalid.");
                }
            }

            if (!fromColumns.SetEquals(
                    ["ApplicationId", "ParentApplicationId", "RetainedFromApplicationId"]))
            {
                throw new InvalidDataException(
                    "ApplicationGroupMember foreign-key contract is invalid.");
            }
        }

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type = 'index'
                  AND name = $indexName
                  AND tbl_name = 'ApplicationGroupMember';
                """;
            command.Parameters.AddWithValue("$indexName", IndexName);
            if (Convert.ToInt32(command.ExecuteScalar()) != 1)
            {
                throw new InvalidDataException(
                    "ApplicationGroupMember parent index is missing.");
            }
        }
    }

    private sealed record ExpectedColumn(
        string Name,
        string Type,
        bool NotNull,
        int PrimaryKeyOrder);
}
