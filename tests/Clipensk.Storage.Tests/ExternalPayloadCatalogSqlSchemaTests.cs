using Clipensk.Storage.ExternalFiles;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ExternalPayloadCatalogSqlSchemaTests
{
    [Fact]
    public void CreateTables_ProducesValidatedCatalogSchema()
    {
        using SqliteConnection connection = OpenMemoryDatabase();
        using SqliteTransaction transaction = connection.BeginTransaction();
        ExternalPayloadCatalogSqlSchema.CreateTables(connection, transaction);
        transaction.Commit();

        ExternalPayloadCatalogSqlSchema.ValidateTables(connection);
    }

    [Fact]
    public void CreateTables_EnforcesShaAndRelativePathUniqueness()
    {
        using SqliteConnection connection = OpenMemoryDatabase();
        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            ExternalPayloadCatalogSqlSchema.CreateTables(connection, transaction);
            transaction.Commit();
        }

        Insert(connection, new string('a', 64), "2026-09-06/a.png", 10);

        Assert.Throws<SqliteException>(() =>
            Insert(connection, new string('b', 64), "2026-09-06/a.png", 11));
        Assert.Throws<SqliteException>(() =>
            Insert(connection, "NOT-A-SHA", "2026-09-06/b.png", 12));
        Assert.Throws<SqliteException>(() =>
            Insert(connection, new string('c', 64), "2026-09-06/c.png", -1));
    }

    [Fact]
    public void ValidateTables_RejectsMissingRelativePathUniquenessIndex()
    {
        using SqliteConnection connection = OpenMemoryDatabase();
        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            ExternalPayloadCatalogSqlSchema.CreateTables(connection, transaction);
            transaction.Commit();
        }

        using (SqliteCommand drop = connection.CreateCommand())
        {
            drop.CommandText = "DROP INDEX UX_ExternalPayloadAddressIndex_RelativePath;";
            drop.ExecuteNonQuery();
        }

        Assert.Throws<InvalidDataException>(() =>
            ExternalPayloadCatalogSqlSchema.ValidateTables(connection));
    }

    private static void Insert(
        SqliteConnection connection,
        string sha256,
        string relativePath,
        long sizeBytes)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ExternalPayloadAddressIndex (Sha256, RelativePath, SizeBytes)
            VALUES ($sha256, $relativePath, $sizeBytes);
            """;
        command.Parameters.AddWithValue("$sha256", sha256);
        command.Parameters.AddWithValue("$relativePath", relativePath);
        command.Parameters.AddWithValue("$sizeBytes", sizeBytes);
        command.ExecuteNonQuery();
    }

    private static SqliteConnection OpenMemoryDatabase()
    {
        SQLitePCL.Batteries.Init();
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }
}
