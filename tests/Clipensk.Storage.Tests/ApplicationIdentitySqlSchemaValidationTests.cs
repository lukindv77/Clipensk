using Clipensk.Storage.Applications;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ApplicationIdentitySqlSchemaValidationTests
{
    private static readonly object ProviderGate = new();
    private static bool _providerInitialized;

    [Fact]
    public void ValidateTables_AcceptsSchemaCreatedByContract()
    {
        using SqliteConnection connection = OpenInMemory();
        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            ApplicationIdentitySqlSchema.CreateTables(connection, transaction);
            transaction.Commit();
        }

        ApplicationIdentitySqlSchema.ValidateTables(connection);
    }

    [Fact]
    public void ValidateTables_RejectsTablesWithCorrectNamesButWrongShape()
    {
        using SqliteConnection connection = OpenInMemory();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE ApplicationIdentity (
                    ApplicationId TEXT NOT NULL PRIMARY KEY
                );
                CREATE TABLE ApplicationIdentityAlias (
                    AliasType TEXT NOT NULL,
                    AliasValue TEXT NOT NULL,
                    ApplicationId TEXT NOT NULL,
                    PRIMARY KEY (AliasType, AliasValue)
                );
                CREATE INDEX IX_ApplicationIdentityAlias_ApplicationId
                    ON ApplicationIdentityAlias(ApplicationId);
                """;
            command.ExecuteNonQuery();
        }

        Assert.Throws<InvalidDataException>(() =>
            ApplicationIdentitySqlSchema.ValidateTables(connection));
    }

    [Fact]
    public void ValidateTables_RejectsMissingCascadeForeignKey()
    {
        using SqliteConnection connection = OpenInMemory();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE ApplicationIdentity (
                    ApplicationId TEXT NOT NULL PRIMARY KEY,
                    CreatedAtUtc TEXT NOT NULL
                );
                CREATE TABLE ApplicationIdentityAlias (
                    AliasType TEXT NOT NULL,
                    AliasValue TEXT NOT NULL,
                    ApplicationId TEXT NOT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    PRIMARY KEY (AliasType, AliasValue),
                    FOREIGN KEY (ApplicationId)
                        REFERENCES ApplicationIdentity(ApplicationId)
                );
                CREATE INDEX IX_ApplicationIdentityAlias_ApplicationId
                    ON ApplicationIdentityAlias(ApplicationId);
                """;
            command.ExecuteNonQuery();
        }

        Assert.Throws<InvalidDataException>(() =>
            ApplicationIdentitySqlSchema.ValidateTables(connection));
    }

    private static SqliteConnection OpenInMemory()
    {
        lock (ProviderGate)
        {
            if (!_providerInitialized)
            {
                SQLitePCL.Batteries.Init();
                _providerInitialized = true;
            }
        }

        var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
        connection.Open();
        return connection;
    }
}
