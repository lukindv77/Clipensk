using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Applications;

/// <summary>
/// Current v12 application groups, per <c>docs/APPLICATION_GROUP_PROTOCOL.md</c> §2.1: a group with
/// its own standalone policy, and one membership row per application in a user group (none means
/// the default group). Groups live only in Current; Archive v1 does not know them.
/// </summary>
internal static class ApplicationGroupSqlSchema
{
    public const int MinimumCurrentSchemaVersion = 12;
    public const string MembershipIndexName = "IX_ApplicationGroupMembership_GroupId";

    public static void CreateTables(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            CREATE TABLE ApplicationGroup (
                GroupId TEXT NOT NULL PRIMARY KEY,
                Name TEXT NOT NULL CHECK (length(Name) > 0),
                NameKey TEXT NOT NULL UNIQUE,
                CaptureRule TEXT NOT NULL CHECK (CaptureRule IN ('Allow', 'Deny')),
                CreatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE ApplicationGroupFormatCapturePolicy (
                GroupId TEXT NOT NULL,
                FormatName TEXT NOT NULL CHECK (length(FormatName) > 0),
                CaptureRule TEXT NOT NULL CHECK (CaptureRule IN ('Allow', 'Deny')),
                MaxBytes INTEGER NULL CHECK (MaxBytes IS NULL OR MaxBytes > 0),
                PRIMARY KEY (GroupId, FormatName),
                FOREIGN KEY (GroupId) REFERENCES ApplicationGroup(GroupId)
            );

            CREATE TABLE ApplicationGroupMembership (
                ApplicationId TEXT NOT NULL PRIMARY KEY,
                GroupId TEXT NOT NULL,
                JoinedAtUtc TEXT NOT NULL,
                FOREIGN KEY (ApplicationId) REFERENCES ApplicationIdentity(ApplicationId),
                FOREIGN KEY (GroupId) REFERENCES ApplicationGroup(GroupId)
            );

            CREATE INDEX {MembershipIndexName}
                ON ApplicationGroupMembership(GroupId);
            """;
        command.ExecuteNonQuery();
    }

    public static void ValidateTables(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ValidateColumns(
            connection,
            "ApplicationGroup",
            [
                new("GroupId", "TEXT", NotNull: true, PrimaryKeyOrder: 1),
                new("Name", "TEXT", NotNull: true, PrimaryKeyOrder: 0),
                new("NameKey", "TEXT", NotNull: true, PrimaryKeyOrder: 0),
                new("CaptureRule", "TEXT", NotNull: true, PrimaryKeyOrder: 0),
                new("CreatedAtUtc", "TEXT", NotNull: true, PrimaryKeyOrder: 0),
            ]);
        ValidateColumns(
            connection,
            "ApplicationGroupFormatCapturePolicy",
            [
                new("GroupId", "TEXT", NotNull: true, PrimaryKeyOrder: 1),
                new("FormatName", "TEXT", NotNull: true, PrimaryKeyOrder: 2),
                new("CaptureRule", "TEXT", NotNull: true, PrimaryKeyOrder: 0),
                new("MaxBytes", "INTEGER", NotNull: false, PrimaryKeyOrder: 0),
            ]);
        ValidateColumns(
            connection,
            "ApplicationGroupMembership",
            [
                new("ApplicationId", "TEXT", NotNull: true, PrimaryKeyOrder: 1),
                new("GroupId", "TEXT", NotNull: true, PrimaryKeyOrder: 0),
                new("JoinedAtUtc", "TEXT", NotNull: true, PrimaryKeyOrder: 0),
            ]);

        // Groups and identities are removed only by explicit operations that first clear what
        // refers to them; no ON DELETE action may reshape memberships or policies implicitly.
        ValidateForeignKeys(
            connection,
            "ApplicationGroupFormatCapturePolicy",
            [new("GroupId", "ApplicationGroup", "GroupId")]);
        ValidateForeignKeys(
            connection,
            "ApplicationGroupMembership",
            [
                new("ApplicationId", "ApplicationIdentity", "ApplicationId"),
                new("GroupId", "ApplicationGroup", "GroupId"),
            ]);

        RequireUniqueIndexOn(connection, "ApplicationGroup", "NameKey");
        using SqliteCommand index = connection.CreateCommand();
        index.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'index'
              AND name = $indexName
              AND tbl_name = 'ApplicationGroupMembership';
            """;
        index.Parameters.AddWithValue("$indexName", MembershipIndexName);
        if (Convert.ToInt32(index.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
        {
            throw new InvalidDataException("ApplicationGroupMembership group index is missing.");
        }
    }

    private static void ValidateColumns(
        SqliteConnection connection,
        string table,
        IReadOnlyList<ExpectedColumn> expected)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{table}');";
        using SqliteDataReader reader = command.ExecuteReader();
        int index = 0;
        while (reader.Read())
        {
            if (index >= expected.Count)
            {
                throw new InvalidDataException($"{table} contains unexpected columns.");
            }

            ExpectedColumn column = expected[index++];
            if (!string.Equals(reader.GetString(1), column.Name, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(2), column.Type, StringComparison.OrdinalIgnoreCase) ||
                reader.GetInt32(3) != (column.NotNull ? 1 : 0) ||
                reader.GetInt32(5) != column.PrimaryKeyOrder)
            {
                throw new InvalidDataException($"{table} column contract is invalid.");
            }
        }

        if (index != expected.Count)
        {
            throw new InvalidDataException($"{table} is missing required columns.");
        }
    }

    private static void ValidateForeignKeys(
        SqliteConnection connection,
        string table,
        IReadOnlyList<ExpectedForeignKey> expected)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_key_list('{table}');";
        using SqliteDataReader reader = command.ExecuteReader();
        var actual = new HashSet<ExpectedForeignKey>();
        while (reader.Read())
        {
            if (!string.Equals(reader.GetString(5), "NO ACTION", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(reader.GetString(6), "NO ACTION", StringComparison.OrdinalIgnoreCase) ||
                !actual.Add(new ExpectedForeignKey(reader.GetString(3), reader.GetString(2), reader.GetString(4))))
            {
                throw new InvalidDataException($"{table} foreign-key contract is invalid.");
            }
        }

        if (!actual.SetEquals(expected))
        {
            throw new InvalidDataException($"{table} foreign-key contract is invalid.");
        }
    }

    private static void RequireUniqueIndexOn(SqliteConnection connection, string table, string column)
    {
        using SqliteCommand list = connection.CreateCommand();
        list.CommandText = $"PRAGMA index_list('{table}');";
        var uniqueIndexes = new List<string>();
        using (SqliteDataReader reader = list.ExecuteReader())
        {
            while (reader.Read())
            {
                if (reader.GetInt32(2) == 1)
                {
                    uniqueIndexes.Add(reader.GetString(1));
                }
            }
        }

        foreach (string indexName in uniqueIndexes)
        {
            using SqliteCommand info = connection.CreateCommand();
            info.CommandText = $"PRAGMA index_info('{indexName.Replace("'", "''", StringComparison.Ordinal)}');";
            using SqliteDataReader reader = info.ExecuteReader();
            var columns = new List<string>();
            while (reader.Read())
            {
                columns.Add(reader.GetString(2));
            }
            if (columns.Count == 1 && string.Equals(columns[0], column, StringComparison.Ordinal))
            {
                return;
            }
        }

        throw new InvalidDataException($"{table}.{column} must be unique.");
    }

    private sealed record ExpectedColumn(
        string Name,
        string Type,
        bool NotNull,
        int PrimaryKeyOrder);

    private sealed record ExpectedForeignKey(string FromColumn, string Table, string ToColumn);
}
