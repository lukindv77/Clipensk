using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Applications;

internal static class ApplicationDiscoveredFormatSqlSchema
{
    public const int MinimumCurrentSchemaVersion = 8;

    public static void CreateTable(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE ApplicationDiscoveredFormat (
                ApplicationId TEXT NOT NULL,
                FormatName TEXT NOT NULL CHECK (length(FormatName) > 0),
                FirstSeenAtUtc TEXT NOT NULL,
                LastSeenAtUtc TEXT NOT NULL,
                PRIMARY KEY (ApplicationId, FormatName),
                FOREIGN KEY (ApplicationId)
                    REFERENCES ApplicationIdentity(ApplicationId)
                    ON DELETE CASCADE
            );
            """;
        command.ExecuteNonQuery();
    }

    public static void ValidateTable(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info('ApplicationDiscoveredFormat');";
            using SqliteDataReader reader = command.ExecuteReader();
            ExpectedColumn[] expected =
            [
                new("ApplicationId", "TEXT", NotNull: true, PrimaryKeyOrder: 1),
                new("FormatName", "TEXT", NotNull: true, PrimaryKeyOrder: 2),
                new("FirstSeenAtUtc", "TEXT", NotNull: true, PrimaryKeyOrder: 0),
                new("LastSeenAtUtc", "TEXT", NotNull: true, PrimaryKeyOrder: 0),
            ];

            int index = 0;
            while (reader.Read())
            {
                if (index >= expected.Length)
                {
                    throw new InvalidDataException(
                        "ApplicationDiscoveredFormat contains unexpected columns.");
                }

                ExpectedColumn column = expected[index++];
                if (!string.Equals(reader.GetString(1), column.Name, StringComparison.Ordinal) ||
                    !string.Equals(reader.GetString(2), column.Type, StringComparison.OrdinalIgnoreCase) ||
                    reader.GetInt32(3) != (column.NotNull ? 1 : 0) ||
                    reader.GetInt32(5) != column.PrimaryKeyOrder)
                {
                    throw new InvalidDataException(
                        "ApplicationDiscoveredFormat column contract is invalid.");
                }
            }

            if (index != expected.Length)
            {
                throw new InvalidDataException(
                    "ApplicationDiscoveredFormat is missing required columns.");
            }
        }

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_key_list('ApplicationDiscoveredFormat');";
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read() ||
                !string.Equals(reader.GetString(2), "ApplicationIdentity", StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(3), "ApplicationId", StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(4), "ApplicationId", StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(6), "CASCADE", StringComparison.OrdinalIgnoreCase) ||
                reader.Read())
            {
                throw new InvalidDataException(
                    "ApplicationDiscoveredFormat foreign-key contract is invalid.");
            }
        }
    }

    private sealed record ExpectedColumn(
        string Name,
        string Type,
        bool NotNull,
        int PrimaryKeyOrder);
}
