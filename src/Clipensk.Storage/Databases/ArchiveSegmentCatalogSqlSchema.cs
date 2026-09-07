using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Databases;

internal static class ArchiveSegmentCatalogSqlSchema
{
    public const int RequiredCatalogSchemaVersion = 3;

    public static void CreateTable(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE ArchiveSegmentIndex (
                DatabaseId TEXT NOT NULL PRIMARY KEY CHECK (length(DatabaseId) = 36),
                FileName TEXT NOT NULL CHECK (length(FileName) > 0),
                CoverageStartDate TEXT NOT NULL CHECK (length(CoverageStartDate) = 10),
                CoverageEndDate TEXT NOT NULL
                    CHECK (length(CoverageEndDate) = 10 AND CoverageEndDate >= CoverageStartDate),
                IsSealed INTEGER NOT NULL CHECK (IsSealed IN (0, 1))
            );

            CREATE UNIQUE INDEX UX_ArchiveSegmentIndex_FileName
                ON ArchiveSegmentIndex(FileName);

            CREATE INDEX IX_ArchiveSegmentIndex_Coverage
                ON ArchiveSegmentIndex(CoverageStartDate, CoverageEndDate);
            """;
        command.ExecuteNonQuery();
    }

    public static void ValidateTable(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using (SqliteCommand tableInfo = connection.CreateCommand())
        {
            tableInfo.CommandText = "PRAGMA table_info('ArchiveSegmentIndex');";
            using SqliteDataReader reader = tableInfo.ExecuteReader();

            ExpectedColumn[] expected =
            [
                new("DatabaseId", "TEXT", true, 1),
                new("FileName", "TEXT", true, 0),
                new("CoverageStartDate", "TEXT", true, 0),
                new("CoverageEndDate", "TEXT", true, 0),
                new("IsSealed", "INTEGER", true, 0),
            ];

            int index = 0;
            while (reader.Read())
            {
                if (index >= expected.Length)
                {
                    throw new InvalidDataException(
                        "ArchiveSegmentIndex contains unexpected columns.");
                }

                ExpectedColumn column = expected[index++];
                if (!string.Equals(reader.GetString(1), column.Name, StringComparison.Ordinal) ||
                    !string.Equals(reader.GetString(2), column.Type, StringComparison.OrdinalIgnoreCase) ||
                    reader.GetInt32(3) != (column.NotNull ? 1 : 0) ||
                    reader.GetInt32(5) != column.PrimaryKeyOrder)
                {
                    throw new InvalidDataException(
                        "ArchiveSegmentIndex column contract is invalid.");
                }
            }

            if (index != expected.Length)
            {
                throw new InvalidDataException(
                    "ArchiveSegmentIndex is missing required columns.");
            }
        }

        ValidateIndex(
            connection,
            "UX_ArchiveSegmentIndex_FileName",
            unique: true,
            ["FileName"]);
        ValidateIndex(
            connection,
            "IX_ArchiveSegmentIndex_Coverage",
            unique: false,
            ["CoverageStartDate", "CoverageEndDate"]);
    }

    private static void ValidateIndex(
        SqliteConnection connection,
        string indexName,
        bool unique,
        IReadOnlyList<string> expectedColumns)
    {
        bool found = false;
        using (SqliteCommand indexList = connection.CreateCommand())
        {
            indexList.CommandText = "PRAGMA index_list('ArchiveSegmentIndex');";
            using SqliteDataReader reader = indexList.ExecuteReader();
            while (reader.Read())
            {
                if (!string.Equals(reader.GetString(1), indexName, StringComparison.Ordinal))
                {
                    continue;
                }

                found = true;
                if ((reader.GetInt32(2) == 1) != unique)
                {
                    throw new InvalidDataException(
                        $"Archive segment index '{indexName}' uniqueness contract is invalid.");
                }
            }
        }

        if (!found)
        {
            throw new InvalidDataException(
                $"Archive segment index '{indexName}' is missing.");
        }

        using SqliteCommand indexInfo = connection.CreateCommand();
        indexInfo.CommandText = $"PRAGMA index_info('{indexName}');";
        using SqliteDataReader indexReader = indexInfo.ExecuteReader();
        int columnIndex = 0;
        while (indexReader.Read())
        {
            if (columnIndex >= expectedColumns.Count ||
                !string.Equals(
                    indexReader.GetString(2),
                    expectedColumns[columnIndex],
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Archive segment index '{indexName}' column contract is invalid.");
            }

            columnIndex++;
        }

        if (columnIndex != expectedColumns.Count)
        {
            throw new InvalidDataException(
                $"Archive segment index '{indexName}' is missing required columns.");
        }
    }

    private sealed record ExpectedColumn(
        string Name,
        string Type,
        bool NotNull,
        int PrimaryKeyOrder);
}