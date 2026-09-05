using Clipensk.Storage.Applications;
using Clipensk.Storage.History;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ClipboardHistorySqlSchemaTests
{
    [Fact]
    public void CreateTables_ProducesValidatedHistorySchema()
    {
        using SqliteConnection connection = OpenMemoryDatabase();
        using SqliteTransaction transaction = connection.BeginTransaction();
        CreateApplicationIdentityTables(connection, transaction);
        ClipboardHistorySqlSchema.CreateTables(connection, transaction);
        transaction.Commit();

        ClipboardHistorySqlSchema.ValidateTables(connection);
    }

    [Fact]
    public void ValidateTables_RejectsMissingPayloadCascade()
    {
        using SqliteConnection connection = OpenMemoryDatabase();
        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            CreateApplicationIdentityTables(connection, transaction);
            ClipboardHistorySqlSchema.CreateTables(connection, transaction);
            transaction.Commit();
        }

        using (SqliteCommand foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_keys = OFF;";
            foreignKeys.ExecuteNonQuery();
        }

        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                ALTER TABLE ClipboardHistoryPayload RENAME TO ClipboardHistoryPayload_Old;

                CREATE TABLE ClipboardHistoryPayload (
                    EventId TEXT NOT NULL,
                    PayloadOrder INTEGER NOT NULL CHECK (PayloadOrder >= 0),
                    FormatName TEXT NOT NULL,
                    PayloadKind TEXT NOT NULL,
                    CanonicalByteCount INTEGER NOT NULL CHECK (CanonicalByteCount >= 0),
                    InlineCanonicalText TEXT NULL,
                    SearchText TEXT NULL,
                    ExternalSha256 TEXT NULL,
                    ExternalRelativePath TEXT NULL,
                    ExternalSizeBytes INTEGER NULL,
                    PRIMARY KEY (EventId, PayloadOrder),
                    FOREIGN KEY (EventId)
                        REFERENCES ClipboardHistoryEvent(EventId)
                        ON DELETE NO ACTION
                );

                CREATE INDEX IX_ClipboardHistoryPayload_FormatName
                    ON ClipboardHistoryPayload(FormatName);

                DROP TABLE ClipboardHistoryPayload_Old;
                """;
            command.ExecuteNonQuery();
            transaction.Commit();
        }

        Assert.Throws<InvalidDataException>(() => ClipboardHistorySqlSchema.ValidateTables(connection));
    }

    private static SqliteConnection OpenMemoryDatabase()
    {
        SQLitePCL.Batteries.Init();
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using SqliteCommand foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_keys = ON;";
        foreignKeys.ExecuteNonQuery();
        return connection;
    }

    private static void CreateApplicationIdentityTables(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        ApplicationIdentitySqlSchema.CreateTables(connection, transaction);
    }
}
