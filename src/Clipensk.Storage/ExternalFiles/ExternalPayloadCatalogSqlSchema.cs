using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.ExternalFiles;

internal static class ExternalPayloadCatalogSqlSchema
{
    public const int RequiredCatalogSchemaVersion = 2;

    public static void CreateTables(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE ExternalPayloadAddressIndex (
                Sha256 TEXT NOT NULL PRIMARY KEY
                    CHECK (length(Sha256) = 64 AND Sha256 NOT GLOB '*[^0-9a-f]*'),
                RelativePath TEXT NOT NULL CHECK (length(RelativePath) > 0),
                SizeBytes INTEGER NOT NULL CHECK (SizeBytes >= 0)
            );

            CREATE UNIQUE INDEX UX_ExternalPayloadAddressIndex_RelativePath
                ON ExternalPayloadAddressIndex(RelativePath);
            """;
        command.ExecuteNonQuery();
    }

    public static void ValidateTables(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using (SqliteCommand tableInfo = connection.CreateCommand())
        {
            tableInfo.CommandText = "PRAGMA table_info('ExternalPayloadAddressIndex');";
            using SqliteDataReader reader = tableInfo.ExecuteReader();

            ExpectedColumn[] expected =
            [
                new("Sha256", "TEXT", true, 1),
                new("RelativePath", "TEXT", true, 0),
                new("SizeBytes", "INTEGER", true, 0),
            ];

            int index = 0;
            while (reader.Read())
            {
                if (index >= expected.Length)
                {
                    throw new InvalidDataException(
                        "ExternalPayloadAddressIndex contains unexpected columns.");
                }

                ExpectedColumn column = expected[index++];
                if (!string.Equals(reader.GetString(1), column.Name, StringComparison.Ordinal) ||
                    !string.Equals(reader.GetString(2), column.Type, StringComparison.OrdinalIgnoreCase) ||
                    reader.GetInt32(3) != (column.NotNull ? 1 : 0) ||
                    reader.GetInt32(5) != column.PrimaryKeyOrder)
                {
                    throw new InvalidDataException(
                        "ExternalPayloadAddressIndex column contract is invalid.");
                }
            }

            if (index != expected.Length)
            {
                throw new InvalidDataException(
                    "ExternalPayloadAddressIndex is missing required columns.");
            }
        }

        using SqliteCommand indexCommand = connection.CreateCommand();
        indexCommand.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'index'
              AND name = 'UX_ExternalPayloadAddressIndex_RelativePath'
              AND tbl_name = 'ExternalPayloadAddressIndex';
            """;
        if (Convert.ToInt32(indexCommand.ExecuteScalar()) != 1)
        {
            throw new InvalidDataException(
                "External payload relative-path uniqueness index is missing.");
        }
    }

    private sealed record ExpectedColumn(
        string Name,
        string Type,
        bool NotNull,
        int PrimaryKeyOrder);
}
