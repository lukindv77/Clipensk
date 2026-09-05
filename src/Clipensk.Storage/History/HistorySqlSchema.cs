using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.History;

internal static class HistorySqlSchema
{
    public const int MinimumCurrentSchemaVersion = 4;

    public const string TextPayloadKind = "Text";
    public const string LinkPayloadKind = "Link";
    public const string StorageItemsPayloadKind = "StorageItems";
    public const string PngExternalPayloadKind = "PngExternal";
    public const string CustomBinaryExternalPayloadKind = "CustomBinaryExternal";

    public static void CreateTables(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE HistoryEntry (
                HistoryEntryId TEXT NOT NULL PRIMARY KEY,
                UtcTimestamp TEXT NOT NULL,
                LocalOffsetMinutes INTEGER NOT NULL,
                WindowsTimeZoneId TEXT NOT NULL CHECK (length(WindowsTimeZoneId) > 0),
                CalendarDate TEXT NOT NULL CHECK (length(CalendarDate) = 10),
                SourceApplicationId TEXT NULL,
                SourceApplicationUserModelId TEXT NULL,
                SourceExecutablePath TEXT NULL
            );

            CREATE TABLE HistoryPayload (
                HistoryEntryId TEXT NOT NULL,
                Ordinal INTEGER NOT NULL CHECK (Ordinal >= 0),
                FormatName TEXT NOT NULL CHECK (length(FormatName) > 0),
                PayloadKind TEXT NOT NULL
                    CHECK (PayloadKind IN (
                        'Text',
                        'Link',
                        'StorageItems',
                        'PngExternal',
                        'CustomBinaryExternal')),
                CanonicalByteCount INTEGER NOT NULL CHECK (CanonicalByteCount >= 0),
                TextValue TEXT NULL,
                SearchText TEXT NULL,
                ExternalSha256 TEXT NULL,
                ExternalRelativePath TEXT NULL,
                ExternalSizeBytes INTEGER NULL,
                PRIMARY KEY (HistoryEntryId, Ordinal),
                FOREIGN KEY (HistoryEntryId)
                    REFERENCES HistoryEntry(HistoryEntryId)
                    ON DELETE CASCADE,
                CHECK (SearchText IS NULL OR PayloadKind = 'Text'),
                CHECK (
                    (
                        PayloadKind IN ('Text', 'Link', 'StorageItems')
                        AND TextValue IS NOT NULL
                        AND ExternalSha256 IS NULL
                        AND ExternalRelativePath IS NULL
                        AND ExternalSizeBytes IS NULL
                    )
                    OR
                    (
                        PayloadKind IN ('PngExternal', 'CustomBinaryExternal')
                        AND TextValue IS NULL
                        AND SearchText IS NULL
                        AND ExternalSha256 IS NOT NULL
                        AND length(ExternalSha256) = 64
                        AND ExternalRelativePath IS NOT NULL
                        AND length(ExternalRelativePath) > 0
                        AND ExternalSizeBytes IS NOT NULL
                        AND ExternalSizeBytes >= 0
                        AND CanonicalByteCount = ExternalSizeBytes
                    )
                )
            );

            CREATE INDEX IX_HistoryEntry_CalendarDate_UtcTimestamp_Id
                ON HistoryEntry(CalendarDate, UtcTimestamp, HistoryEntryId);

            CREATE INDEX IX_HistoryEntry_UtcTimestamp_Id
                ON HistoryEntry(UtcTimestamp, HistoryEntryId);

            CREATE INDEX IX_HistoryEntry_SourceApplicationId_UtcTimestamp_Id
                ON HistoryEntry(SourceApplicationId, UtcTimestamp, HistoryEntryId)
                WHERE SourceApplicationId IS NOT NULL;
            """;
        command.ExecuteNonQuery();
    }

    public static void ValidateTables(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ValidateColumns(
            connection,
            "HistoryEntry",
            [
                new ExpectedColumn("HistoryEntryId", "TEXT", NotNull: true, PrimaryKeyOrder: 1),
                new ExpectedColumn("UtcTimestamp", "TEXT", NotNull: true, PrimaryKeyOrder: 0),
                new ExpectedColumn("LocalOffsetMinutes", "INTEGER", NotNull: true, PrimaryKeyOrder: 0),
                new ExpectedColumn("WindowsTimeZoneId", "TEXT", NotNull: true, PrimaryKeyOrder: 0),
                new ExpectedColumn("CalendarDate", "TEXT", NotNull: true, PrimaryKeyOrder: 0),
                new ExpectedColumn("SourceApplicationId", "TEXT", NotNull: false, PrimaryKeyOrder: 0),
                new ExpectedColumn("SourceApplicationUserModelId", "TEXT", NotNull: false, PrimaryKeyOrder: 0),
                new ExpectedColumn("SourceExecutablePath", "TEXT", NotNull: false, PrimaryKeyOrder: 0),
            ]);

        ValidateColumns(
            connection,
            "HistoryPayload",
            [
                new ExpectedColumn("HistoryEntryId", "TEXT", NotNull: true, PrimaryKeyOrder: 1),
                new ExpectedColumn("Ordinal", "INTEGER", NotNull: true, PrimaryKeyOrder: 2),
                new ExpectedColumn("FormatName", "TEXT", NotNull: true, PrimaryKeyOrder: 0),
                new ExpectedColumn("PayloadKind", "TEXT", NotNull: true, PrimaryKeyOrder: 0),
                new ExpectedColumn("CanonicalByteCount", "INTEGER", NotNull: true, PrimaryKeyOrder: 0),
                new ExpectedColumn("TextValue", "TEXT", NotNull: false, PrimaryKeyOrder: 0),
                new ExpectedColumn("SearchText", "TEXT", NotNull: false, PrimaryKeyOrder: 0),
                new ExpectedColumn("ExternalSha256", "TEXT", NotNull: false, PrimaryKeyOrder: 0),
                new ExpectedColumn("ExternalRelativePath", "TEXT", NotNull: false, PrimaryKeyOrder: 0),
                new ExpectedColumn("ExternalSizeBytes", "INTEGER", NotNull: false, PrimaryKeyOrder: 0),
            ]);

        ValidateSingleForeignKey(
            connection,
            tableName: "HistoryPayload",
            referencedTable: "HistoryEntry",
            fromColumn: "HistoryEntryId",
            toColumn: "HistoryEntryId");

        ValidateIndex(connection, "IX_HistoryEntry_CalendarDate_UtcTimestamp_Id", "HistoryEntry");
        ValidateIndex(connection, "IX_HistoryEntry_UtcTimestamp_Id", "HistoryEntry");
        ValidateIndex(connection, "IX_HistoryEntry_SourceApplicationId_UtcTimestamp_Id", "HistoryEntry");
    }

    private static void ValidateSingleForeignKey(
        SqliteConnection connection,
        string tableName,
        string referencedTable,
        string fromColumn,
        string toColumn)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_key_list('{tableName}');";
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read() ||
            !string.Equals(reader.GetString(2), referencedTable, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(3), fromColumn, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(4), toColumn, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(6), "CASCADE", StringComparison.OrdinalIgnoreCase) ||
            reader.Read())
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
            throw new InvalidDataException($"Required history index {indexName} is missing.");
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
