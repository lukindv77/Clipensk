using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Clipboard;

internal static class GlobalCapturePolicyMaintenanceSqlSchema
{
    public const int MinimumCurrentSchemaVersion = 7;

    public static void CreateTable(SqliteConnection connection, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE GlobalCapturePolicyMaintenance (
                SingletonId INTEGER NOT NULL PRIMARY KEY CHECK (SingletonId = 1),
                OperationId TEXT NOT NULL CHECK (length(OperationId) > 0),
                Phase TEXT NOT NULL CHECK (Phase IN ('ArchiveCleanup', 'CatalogRebuild', 'TrashCollection')),
                StartedAtUtc TEXT NOT NULL CHECK (length(StartedAtUtc) > 0)
            );
            """;
        command.ExecuteNonQuery();
    }

    public static void ValidateTable(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info('GlobalCapturePolicyMaintenance');";
        using SqliteDataReader reader = command.ExecuteReader();

        ExpectedColumn[] expected =
        [
            new("SingletonId", "INTEGER", NotNull: true, PrimaryKeyOrder: 1),
            new("OperationId", "TEXT", NotNull: true, PrimaryKeyOrder: 0),
            new("Phase", "TEXT", NotNull: true, PrimaryKeyOrder: 0),
            new("StartedAtUtc", "TEXT", NotNull: true, PrimaryKeyOrder: 0),
        ];

        int index = 0;
        while (reader.Read())
        {
            if (index >= expected.Length)
            {
                throw new InvalidDataException(
                    "GlobalCapturePolicyMaintenance contains unexpected columns.");
            }

            ExpectedColumn column = expected[index++];
            if (!string.Equals(reader.GetString(1), column.Name, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(2), column.Type, StringComparison.OrdinalIgnoreCase) ||
                reader.GetInt32(3) != (column.NotNull ? 1 : 0) ||
                reader.GetInt32(5) != column.PrimaryKeyOrder)
            {
                throw new InvalidDataException(
                    "GlobalCapturePolicyMaintenance column contract is invalid.");
            }
        }

        if (index != expected.Length)
        {
            throw new InvalidDataException(
                "GlobalCapturePolicyMaintenance is missing required columns.");
        }
    }

    private sealed record ExpectedColumn(
        string Name,
        string Type,
        bool NotNull,
        int PrimaryKeyOrder);
}