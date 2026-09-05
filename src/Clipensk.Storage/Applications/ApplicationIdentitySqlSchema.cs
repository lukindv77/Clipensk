using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Applications;

internal static class ApplicationIdentitySqlSchema
{
    public const int RequiredCurrentSchemaVersion = 2;
    public const string AumidAliasType = "Aumid";
    public const string ExecutablePathAliasType = "ExecutablePath";

    public static void CreateTables(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE ApplicationIdentity (
                ApplicationId TEXT NOT NULL PRIMARY KEY,
                CreatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE ApplicationIdentityAlias (
                AliasType TEXT NOT NULL
                    CHECK (AliasType IN ('Aumid', 'ExecutablePath')),
                AliasValue TEXT NOT NULL,
                ApplicationId TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                PRIMARY KEY (AliasType, AliasValue),
                FOREIGN KEY (ApplicationId)
                    REFERENCES ApplicationIdentity(ApplicationId)
                    ON DELETE CASCADE
            );

            CREATE INDEX IX_ApplicationIdentityAlias_ApplicationId
                ON ApplicationIdentityAlias(ApplicationId);
            """;
        command.ExecuteNonQuery();
    }
}
