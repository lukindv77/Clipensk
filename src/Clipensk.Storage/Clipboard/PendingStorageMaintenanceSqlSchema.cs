using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Clipboard;

internal static class PendingStorageMaintenanceSqlSchema
{
    public const int MinimumCurrentSchemaVersion = 7;
    public const string PolicyMutationOperationKind = "PolicyMutation";

    public static void CreateTable(SqliteConnection connection, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE PendingStorageMaintenance (
                SingletonId INTEGER NOT NULL PRIMARY KEY CHECK (SingletonId = 1),
                OperationId TEXT NOT NULL,
                OperationKind TEXT NOT NULL CHECK (OperationKind = 'PolicyMutation'),
                CreatedAtUtc TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public static void ValidateTable(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ExpectedColumn[] expected =
        [
            new("SingletonId", "INTEGER", true, 1),
            new("OperationId", "TEXT", true, 0),
            new("OperationKind", "TEXT", true, 0),
            new("CreatedAtUtc", "TEXT", true, 0),
        ];

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info('PendingStorageMaintenance');";
            using SqliteDataReader reader = command.ExecuteReader();
            int index = 0;
            while (reader.Read())
            {
                if (index >= expected.Length)
                {
                    throw new InvalidDataException("PendingStorageMaintenance contains unexpected columns.");
                }

                ExpectedColumn column = expected[index++];
                if (!string.Equals(reader.GetString(1), column.Name, StringComparison.Ordinal) ||
                    !string.Equals(reader.GetString(2), column.Type, StringComparison.OrdinalIgnoreCase) ||
                    reader.GetInt32(3) != (column.NotNull ? 1 : 0) ||
                    reader.GetInt32(5) != column.PrimaryKeyOrder)
                {
                    throw new InvalidDataException("PendingStorageMaintenance column contract is invalid.");
                }
            }

            if (index != expected.Length)
            {
                throw new InvalidDataException("PendingStorageMaintenance is missing required columns.");
            }
        }

        using SqliteCommand rows = connection.CreateCommand();
        rows.CommandText = "SELECT SingletonId, OperationId, OperationKind, CreatedAtUtc FROM PendingStorageMaintenance;";
        using SqliteDataReader rowReader = rows.ExecuteReader();
        if (!rowReader.Read())
        {
            return;
        }

        if (rowReader.GetInt64(0) != 1 ||
            !Guid.TryParse(rowReader.GetString(1), out Guid operationId) || operationId == Guid.Empty ||
            !string.Equals(rowReader.GetString(2), PolicyMutationOperationKind, StringComparison.Ordinal) ||
            !DateTimeOffset.TryParse(
                rowReader.GetString(3),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _))
        {
            throw new InvalidDataException("PendingStorageMaintenance contains invalid operation metadata.");
        }

        if (rowReader.Read())
        {
            throw new InvalidDataException("PendingStorageMaintenance must contain at most one row.");
        }
    }

    private sealed record ExpectedColumn(string Name, string Type, bool NotNull, int PrimaryKeyOrder);
}
