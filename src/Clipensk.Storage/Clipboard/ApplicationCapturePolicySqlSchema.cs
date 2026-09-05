using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Clipboard;

internal static class ApplicationCapturePolicySqlSchema
{
    public const int MinimumCurrentSchemaVersion = 3;

    public static void CreateTables(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE ApplicationCapturePolicy (
                ApplicationId TEXT NOT NULL PRIMARY KEY,
                CaptureRule TEXT NOT NULL
                    CHECK (CaptureRule IN ('Inherit', 'Allow', 'Deny')),
                FOREIGN KEY (ApplicationId)
                    REFERENCES ApplicationIdentity(ApplicationId)
                    ON DELETE CASCADE
            );

            CREATE TABLE ApplicationFormatCapturePolicy (
                ApplicationId TEXT NOT NULL,
                FormatName TEXT NOT NULL CHECK (length(FormatName) > 0),
                CaptureRule TEXT NOT NULL
                    CHECK (CaptureRule IN ('Inherit', 'Allow', 'Deny')),
                MaxBytes INTEGER NULL
                    CHECK (MaxBytes IS NULL OR MaxBytes > 0),
                PRIMARY KEY (ApplicationId, FormatName),
                FOREIGN KEY (ApplicationId)
                    REFERENCES ApplicationCapturePolicy(ApplicationId)
                    ON DELETE CASCADE
            );
            """;
        command.ExecuteNonQuery();
    }

    public static void ValidateTables(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ValidateColumns(
            connection,
            "ApplicationCapturePolicy",
            [
                new ExpectedColumn("ApplicationId", "TEXT", NotNull: true, PrimaryKeyOrder: 1),
                new ExpectedColumn("CaptureRule", "TEXT", NotNull: true, PrimaryKeyOrder: 0),
            ]);
        ValidateColumns(
            connection,
            "ApplicationFormatCapturePolicy",
            [
                new ExpectedColumn("ApplicationId", "TEXT", NotNull: true, PrimaryKeyOrder: 1),
                new ExpectedColumn("FormatName", "TEXT", NotNull: true, PrimaryKeyOrder: 2),
                new ExpectedColumn("CaptureRule", "TEXT", NotNull: true, PrimaryKeyOrder: 0),
                new ExpectedColumn("MaxBytes", "INTEGER", NotNull: false, PrimaryKeyOrder: 0),
            ]);

        ValidateSingleForeignKey(
            connection,
            tableName: "ApplicationCapturePolicy",
            referencedTable: "ApplicationIdentity",
            fromColumn: "ApplicationId",
            toColumn: "ApplicationId");
        ValidateSingleForeignKey(
            connection,
            tableName: "ApplicationFormatCapturePolicy",
            referencedTable: "ApplicationCapturePolicy",
            fromColumn: "ApplicationId",
            toColumn: "ApplicationId");
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
