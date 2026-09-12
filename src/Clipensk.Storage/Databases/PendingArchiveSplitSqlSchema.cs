using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Databases;

internal static class PendingArchiveSplitSqlSchema
{
    public const int MinimumCurrentSchemaVersion = 9;

    public static void CreateTables(SqliteConnection connection, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        using (SqliteCommand operation = connection.CreateCommand())
        {
            operation.Transaction = transaction;
            operation.CommandText = """
                CREATE TABLE PendingArchiveSplit (
                    SingletonId INTEGER NOT NULL PRIMARY KEY CHECK (SingletonId = 1),
                    OperationId TEXT NOT NULL UNIQUE CHECK (length(OperationId) > 0),
                    SourceFileName TEXT NOT NULL CHECK (length(SourceFileName) > 0),
                    SourceDatabaseId TEXT NOT NULL CHECK (length(SourceDatabaseId) > 0),
                    SourceCoverageStartDate TEXT NOT NULL CHECK (length(SourceCoverageStartDate) > 0),
                    SourceCoverageEndDate TEXT NOT NULL CHECK (length(SourceCoverageEndDate) > 0),
                    Phase INTEGER NOT NULL CHECK (Phase >= 0 AND Phase <= 3),
                    CreatedAtUtc TEXT NOT NULL CHECK (length(CreatedAtUtc) > 0)
                );
                """;
            operation.ExecuteNonQuery();
        }

        using SqliteCommand segments = connection.CreateCommand();
        segments.Transaction = transaction;
        segments.CommandText = """
            CREATE TABLE PendingArchiveSplitSegment (
                OperationId TEXT NOT NULL,
                SegmentOrder INTEGER NOT NULL CHECK (SegmentOrder >= 0),
                FileName TEXT NOT NULL CHECK (length(FileName) > 0),
                DatabaseId TEXT NOT NULL CHECK (length(DatabaseId) > 0),
                CoverageStartDate TEXT NOT NULL CHECK (length(CoverageStartDate) > 0),
                CoverageEndDate TEXT NOT NULL CHECK (length(CoverageEndDate) > 0),
                PRIMARY KEY (OperationId, SegmentOrder),
                UNIQUE (OperationId, FileName),
                UNIQUE (OperationId, DatabaseId),
                FOREIGN KEY (OperationId) REFERENCES PendingArchiveSplit(OperationId) ON DELETE CASCADE
            );
            """;
        segments.ExecuteNonQuery();
    }

    public static void ValidateTables(SqliteConnection connection)
    {
        ValidateTable(
            connection,
            "PendingArchiveSplit",
            [
                new("SingletonId", "INTEGER", true, 1),
                new("OperationId", "TEXT", true, 0),
                new("SourceFileName", "TEXT", true, 0),
                new("SourceDatabaseId", "TEXT", true, 0),
                new("SourceCoverageStartDate", "TEXT", true, 0),
                new("SourceCoverageEndDate", "TEXT", true, 0),
                new("Phase", "INTEGER", true, 0),
                new("CreatedAtUtc", "TEXT", true, 0),
            ]);
        ValidateTable(
            connection,
            "PendingArchiveSplitSegment",
            [
                new("OperationId", "TEXT", true, 1),
                new("SegmentOrder", "INTEGER", true, 2),
                new("FileName", "TEXT", true, 0),
                new("DatabaseId", "TEXT", true, 0),
                new("CoverageStartDate", "TEXT", true, 0),
                new("CoverageEndDate", "TEXT", true, 0),
            ]);
    }

    private static void ValidateTable(
        SqliteConnection connection,
        string tableName,
        IReadOnlyList<ExpectedColumn> expected)
    {
        ArgumentNullException.ThrowIfNull(connection);
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
