using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Clipboard;

internal static class CustomBinaryFormatConfigurationSqlSchema
{
    public const int MinimumCurrentSchemaVersion = 6;

    public static void CreateTable(SqliteConnection connection, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE CustomBinaryFormatConfiguration (
                FormatName TEXT NOT NULL PRIMARY KEY CHECK (length(FormatName) > 0),
                FileExtension TEXT NOT NULL
                    CHECK (length(FileExtension) > 1 AND substr(FileExtension, 1, 1) = '.')
            );
            """;
        command.ExecuteNonQuery();
    }

    public static void ValidateTable(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info('CustomBinaryFormatConfiguration');";
        using SqliteDataReader reader = command.ExecuteReader();

        ExpectedColumn[] expected =
        [
            new("FormatName", "TEXT", NotNull: true, PrimaryKeyOrder: 1),
            new("FileExtension", "TEXT", NotNull: true, PrimaryKeyOrder: 0),
        ];

        int index = 0;
        while (reader.Read())
        {
            if (index >= expected.Length)
            {
                throw new InvalidDataException(
                    "CustomBinaryFormatConfiguration contains unexpected columns.");
            }

            ExpectedColumn column = expected[index++];
            if (!string.Equals(reader.GetString(1), column.Name, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(2), column.Type, StringComparison.OrdinalIgnoreCase) ||
                reader.GetInt32(3) != (column.NotNull ? 1 : 0) ||
                reader.GetInt32(5) != column.PrimaryKeyOrder)
            {
                throw new InvalidDataException(
                    "CustomBinaryFormatConfiguration column contract is invalid.");
            }
        }

        if (index != expected.Length)
        {
            throw new InvalidDataException(
                "CustomBinaryFormatConfiguration is missing required columns.");
        }
    }

    private sealed record ExpectedColumn(
        string Name,
        string Type,
        bool NotNull,
        int PrimaryKeyOrder);
}
