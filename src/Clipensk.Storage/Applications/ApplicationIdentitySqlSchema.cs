using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Applications;

internal static class ApplicationIdentitySqlSchema
{
    public const int MinimumCurrentSchemaVersion = 2;
    public const string AumidAliasType = "Aumid";
    public const string ExecutablePathAliasType = "ExecutablePath";

    public static void CreateTables(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE ApplicationIdentity (
                ApplicationId TEXT NOT NULL PRIMARY KEY,
                CreatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE ApplicationIdentityAlias (
                AliasType TEXT NOT NULL
                    CHECK (AliasType IN ('Aumid', 'ExecutablePath')),
                AliasValue TEXT NOT NULL,
                ApplicationId TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                PRIMARY KEY (AliasType, AliasValue),
                FOREIGN KEY (ApplicationId)
                    REFERENCES ApplicationIdentity(ApplicationId)
                    ON DELETE CASCADE
            );

            CREATE INDEX IX_ApplicationIdentityAlias_ApplicationId
                ON ApplicationIdentityAlias(ApplicationId);
            """;
        command.ExecuteNonQuery();
    }

    public static void ValidateTables(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ValidateColumns(
            connection,
            "ApplicationIdentity",
            [
                new ExpectedColumn("ApplicationId", "TEXT", NotNull: true, PrimaryKeyOrder: 1),
                new ExpectedColumn("CreatedAtUtc", "TEXT", NotNull: true, PrimaryKeyOrder: 0),
            ]);
        ValidateColumns(
            connection,
            "ApplicationIdentityAlias",
            [
                new ExpectedColumn("AliasType", "TEXT", NotNull: true, PrimaryKeyOrder: 1),
                new ExpectedColumn("AliasValue", "TEXT", NotNull: true, PrimaryKeyOrder: 2),
                new ExpectedColumn("ApplicationId", "TEXT", NotNull: true, PrimaryKeyOrder: 0),
                new ExpectedColumn("CreatedAtUtc", "TEXT", NotNull: true, PrimaryKeyOrder: 0),
            ]);

        using (SqliteCommand foreignKey = connection.CreateCommand())
        {
            foreignKey.CommandText = "PRAGMA foreign_key_list('ApplicationIdentityAlias');";
            using SqliteDataReader reader = foreignKey.ExecuteReader();
            if (!reader.Read() ||
                !string.Equals(reader.GetString(2), "ApplicationIdentity", StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(3), "ApplicationId", StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(4), "ApplicationId", StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(6), "CASCADE", StringComparison.OrdinalIgnoreCase) ||
                reader.Read())
            {
                throw new InvalidDataException(
                    "ApplicationIdentityAlias foreign-key contract is invalid.");
            }
        }

        using (SqliteCommand index = connection.CreateCommand())
        {
            index.CommandText = """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type = 'index'
                  AND name = 'IX_ApplicationIdentityAlias_ApplicationId'
                  AND tbl_name = 'ApplicationIdentityAlias';
                """;
            if (Convert.ToInt32(index.ExecuteScalar()) != 1)
            {
                throw new InvalidDataException(
                    "ApplicationIdentityAlias application-id index is missing.");
            }
        }
    }

    private static void ValidateColumns(
        SqliteConnection connection,
        string tableName,
        IReadOnlyList<ExpectedColumn> expected)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{tableName}');";
        using SqliteDataReader reader = command.ExecuteReader();

        int index = 0;
        while (reader.Read())
        {
            if (index >= expected.Count)
            {
                throw new InvalidDataException($"{tableName} contains unexpected columns.");
            }

            ExpectedColumn column = expected[index++];
            if (!string.Equals(reader.GetString(1), column.Name, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(2), column.Type, StringComparison.OrdinalIgnoreCase) ||
                reader.GetInt32(3) != (column.NotNull ? 1 : 0) ||
                reader.GetInt32(5) != column.PrimaryKeyOrder)
            {
                throw new InvalidDataException($"{tableName} column contract is invalid.");
            }
        }

        if (index != expected.Count)
        {
            throw new InvalidDataException($"{tableName} is missing required columns.");
        }
    }

    private sealed record ExpectedColumn(
        string Name,
        string Type,
        bool NotNull,
        int PrimaryKeyOrder);
}
