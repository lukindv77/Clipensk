using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.History;

internal static class ClipboardHistorySqlSchema
{
    public const int RequiredCurrentSchemaVersion = 3;

    public static void CreateTables(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE ClipboardHistoryEvent (
                EventId TEXT NOT NULL PRIMARY KEY,
                EventUtc TEXT NOT NULL,
                LocalOffsetMinutes INTEGER NOT NULL,
                WindowsTimeZoneId TEXT NOT NULL,
                CalendarDate TEXT NOT NULL,
                SourceApplicationId TEXT NULL,
                SourceProcessId INTEGER NULL,
                SourceExecutablePath TEXT NULL,
                SourceApplicationUserModelId TEXT NULL,
                FOREIGN KEY (SourceApplicationId)
                    REFERENCES ApplicationIdentity(ApplicationId)
                    ON DELETE SET NULL
            );

            CREATE INDEX IX_ClipboardHistoryEvent_CalendarDate_EventUtc
                ON ClipboardHistoryEvent(CalendarDate, EventUtc, EventId);

            CREATE INDEX IX_ClipboardHistoryEvent_SourceApplicationId_EventUtc
                ON ClipboardHistoryEvent(SourceApplicationId, EventUtc, EventId);

            CREATE TABLE ClipboardHistoryPayload (
                EventId TEXT NOT NULL,
                PayloadOrder INTEGER NOT NULL CHECK (PayloadOrder >= 0),
                FormatName TEXT NOT NULL,
                PayloadKind TEXT NOT NULL
                    CHECK (PayloadKind IN ('Text', 'Link', 'PngImage', 'CustomBinary', 'StorageItems')),
                CanonicalByteCount INTEGER NOT NULL CHECK (CanonicalByteCount >= 0),
                InlineCanonicalText TEXT NULL,
                SearchText TEXT NULL,
                ExternalSha256 TEXT NULL,
                ExternalRelativePath TEXT NULL,
                ExternalSizeBytes INTEGER NULL CHECK (ExternalSizeBytes IS NULL OR ExternalSizeBytes >= 0),
                PRIMARY KEY (EventId, PayloadOrder),
                FOREIGN KEY (EventId)
                    REFERENCES ClipboardHistoryEvent(EventId)
                    ON DELETE CASCADE,
                CHECK (
                    (PayloadKind IN ('Text', 'Link', 'StorageItems')
                     AND InlineCanonicalText IS NOT NULL
                     AND ExternalSha256 IS NULL
                     AND ExternalRelativePath IS NULL
                     AND ExternalSizeBytes IS NULL)
                    OR
                    (PayloadKind IN ('PngImage', 'CustomBinary')
                     AND InlineCanonicalText IS NULL
                     AND SearchText IS NULL
                     AND ExternalSha256 IS NOT NULL
                     AND ExternalRelativePath IS NOT NULL
                     AND ExternalSizeBytes IS NOT NULL)
                )
            );

            CREATE INDEX IX_ClipboardHistoryPayload_FormatName
                ON ClipboardHistoryPayload(FormatName);
            """;
        command.ExecuteNonQuery();
    }

    public static void ValidateTables(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ValidateColumns(
            connection,
            "ClipboardHistoryEvent",
            [
                new ExpectedColumn("EventId", "TEXT", true, 1),
                new ExpectedColumn("EventUtc", "TEXT", true, 0),
                new ExpectedColumn("LocalOffsetMinutes", "INTEGER", true, 0),
                new ExpectedColumn("WindowsTimeZoneId", "TEXT", true, 0),
                new ExpectedColumn("CalendarDate", "TEXT", true, 0),
                new ExpectedColumn("SourceApplicationId", "TEXT", false, 0),
                new ExpectedColumn("SourceProcessId", "INTEGER", false, 0),
                new ExpectedColumn("SourceExecutablePath", "TEXT", false, 0),
                new ExpectedColumn("SourceApplicationUserModelId", "TEXT", false, 0),
            ]);

        ValidateColumns(
            connection,
            "ClipboardHistoryPayload",
            [
                new ExpectedColumn("EventId", "TEXT", true, 1),
                new ExpectedColumn("PayloadOrder", "INTEGER", true, 2),
                new ExpectedColumn("FormatName", "TEXT", true, 0),
                new ExpectedColumn("PayloadKind", "TEXT", true, 0),
                new ExpectedColumn("CanonicalByteCount", "INTEGER", true, 0),
                new ExpectedColumn("InlineCanonicalText", "TEXT", false, 0),
                new ExpectedColumn("SearchText", "TEXT", false, 0),
                new ExpectedColumn("ExternalSha256", "TEXT", false, 0),
                new ExpectedColumn("ExternalRelativePath", "TEXT", false, 0),
                new ExpectedColumn("ExternalSizeBytes", "INTEGER", false, 0),
            ]);

        ValidateForeignKey(
            connection,
            "ClipboardHistoryEvent",
            expectedTable: "ApplicationIdentity",
            expectedFrom: "SourceApplicationId",
            expectedTo: "ApplicationId",
            expectedOnDelete: "SET NULL");
        ValidateForeignKey(
            connection,
            "ClipboardHistoryPayload",
            expectedTable: "ClipboardHistoryEvent",
            expectedFrom: "EventId",
            expectedTo: "EventId",
            expectedOnDelete: "CASCADE");

        ValidateIndex(connection, "IX_ClipboardHistoryEvent_CalendarDate_EventUtc", "ClipboardHistoryEvent");
        ValidateIndex(connection, "IX_ClipboardHistoryEvent_SourceApplicationId_EventUtc", "ClipboardHistoryEvent");
        ValidateIndex(connection, "IX_ClipboardHistoryPayload_FormatName", "ClipboardHistoryPayload");
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

    private static void ValidateForeignKey(
        SqliteConnection connection,
        string tableName,
        string expectedTable,
        string expectedFrom,
        string expectedTo,
        string expectedOnDelete)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_key_list('{tableName}');";
        using SqliteDataReader reader = command.ExecuteReader();

        bool found = false;
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(2), expectedTable, StringComparison.Ordinal) &&
                string.Equals(reader.GetString(3), expectedFrom, StringComparison.Ordinal) &&
                string.Equals(reader.GetString(4), expectedTo, StringComparison.Ordinal) &&
                string.Equals(reader.GetString(6), expectedOnDelete, StringComparison.OrdinalIgnoreCase))
            {
                found = true;
            }
        }

        if (!found)
        {
            throw new InvalidDataException($"{tableName} foreign-key contract is invalid.");
        }
    }

    private static void ValidateIndex(
        SqliteConnection connection,
        string indexName,
        string tableName)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'index'
              AND name = $indexName
              AND tbl_name = $tableName;
            """;
        command.Parameters.AddWithValue("$indexName", indexName);
        command.Parameters.AddWithValue("$tableName", tableName);
        if (Convert.ToInt32(command.ExecuteScalar()) != 1)
        {
            throw new InvalidDataException($"{indexName} is missing.");
        }
    }

    private sealed record ExpectedColumn(
        string Name,
        string Type,
        bool NotNull,
        int PrimaryKeyOrder);
}
