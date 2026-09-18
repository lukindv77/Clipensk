using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Databases;

internal static class PendingArchiveRotationSqlSchema
{
    public const int MinimumCurrentSchemaVersion = 10;

    public static void CreateTables(SqliteConnection connection, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        using (SqliteCommand operation = connection.CreateCommand())
        {
            operation.Transaction = transaction;
            operation.CommandText = """
                CREATE TABLE PendingArchiveRotation (
                    SingletonId INTEGER NOT NULL PRIMARY KEY CHECK (SingletonId = 1),
                    OperationId TEXT NOT NULL UNIQUE CHECK (length(OperationId) > 0),
                    MaxRecordCount INTEGER CHECK (MaxRecordCount IS NULL OR MaxRecordCount > 0),
                    MaxBytes INTEGER CHECK (MaxBytes IS NULL OR MaxBytes > 0),
                    MaxCalendarDays INTEGER CHECK (MaxCalendarDays IS NULL OR MaxCalendarDays > 0),
                    ThresholdMode INTEGER CHECK (ThresholdMode IS NULL OR ThresholdMode IN (1, 2)),
                    Phase INTEGER NOT NULL CHECK (Phase >= 0 AND Phase <= 4),
                    CreatedAtUtc TEXT NOT NULL CHECK (length(CreatedAtUtc) > 0),
                    CHECK (MaxRecordCount IS NOT NULL OR MaxBytes IS NOT NULL OR MaxCalendarDays IS NOT NULL)
                );
                """;
            operation.ExecuteNonQuery();
        }

        using SqliteCommand targets = connection.CreateCommand();
        targets.Transaction = transaction;
        targets.CommandText = """
            CREATE TABLE PendingArchiveRotationTarget (
                OperationId TEXT NOT NULL,
                SegmentOrder INTEGER NOT NULL CHECK (SegmentOrder >= 0),
                FileName TEXT NOT NULL CHECK (length(FileName) > 0),
                DatabaseId TEXT NOT NULL CHECK (length(DatabaseId) > 0),
                CoverageStartDate TEXT NOT NULL CHECK (length(CoverageStartDate) > 0),
                CoverageEndDate TEXT NOT NULL CHECK (length(CoverageEndDate) > 0),
                ExpectedRecordCount INTEGER NOT NULL CHECK (ExpectedRecordCount >= 0),
                ShadowPhysicalSizeBytes INTEGER NOT NULL CHECK (ShadowPhysicalSizeBytes > 0),
                PRIMARY KEY (OperationId, SegmentOrder),
                UNIQUE (OperationId, FileName),
                UNIQUE (OperationId, DatabaseId),
                FOREIGN KEY (OperationId) REFERENCES PendingArchiveRotation(OperationId) ON DELETE CASCADE
            );
            """;
        targets.ExecuteNonQuery();
    }

    public static void ValidateTables(SqliteConnection connection)
    {
        ValidateTable(
            connection,
            "PendingArchiveRotation",
            [
                new("SingletonId", "INTEGER", true, 1),
                new("OperationId", "TEXT", true, 0),
                new("MaxRecordCount", "INTEGER", false, 0),
                new("MaxBytes", "INTEGER", false, 0),
                new("MaxCalendarDays", "INTEGER", false, 0),
                new("ThresholdMode", "INTEGER", false, 0),
                new("Phase", "INTEGER", true, 0),
                new("CreatedAtUtc", "TEXT", true, 0),
            ]);
        ValidateTable(
            connection,
            "PendingArchiveRotationTarget",
            [
                new("OperationId", "TEXT", true, 1),
                new("SegmentOrder", "INTEGER", true, 2),
                new("FileName", "TEXT", true, 0),
                new("DatabaseId", "TEXT", true, 0),
                new("CoverageStartDate", "TEXT", true, 0),
                new("CoverageEndDate", "TEXT", true, 0),
                new("ExpectedRecordCount", "INTEGER", true, 0),
                new("ShadowPhysicalSizeBytes", "INTEGER", true, 0),
            ]);
    }

    /// <summary>
    /// Rotation and split markers both live in Current and must never be active at the same time.
    /// The rotation table only exists from Current v10, so an older Current provably has no pending
    /// rotation and the absent table is an exact answer rather than a tolerated gap.
    /// </summary>
    public static bool HasPendingOperation(SqliteConnection connection, SqliteTransaction? transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!TableExists(connection, transaction, "PendingArchiveRotation"))
        {
            return false;
        }

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM PendingArchiveRotation);";
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() && reader.GetInt64(0) != 0;
    }

    private static bool TableExists(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string tableName)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name COLLATE BINARY);";
        command.Parameters.AddWithValue("$name", tableName);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() && reader.GetInt64(0) != 0;
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
