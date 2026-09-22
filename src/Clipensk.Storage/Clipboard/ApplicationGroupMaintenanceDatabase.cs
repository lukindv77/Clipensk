using System.Globalization;
using Clipensk.Core.Storage;
using Clipensk.Storage.Applications;
using Clipensk.Storage.Databases;
using Clipensk.Storage.History;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Clipboard;

/// <summary>
/// Opens Current and Archive databases for the application-group maintenance operations of
/// <c>docs/APPLICATION_GROUP_PROTOCOL.md</c>, with the exact schema contracts they rely on
/// validated before any read or write.
/// </summary>
internal static class ApplicationGroupMaintenanceDatabase
{
    public static string CurrentPath(ProtectedStorageSessionLease session) =>
        Path.Combine(Path.GetFullPath(session.DataRootPath), "Current", "current.db");

    public static string ArchiveDirectory(ProtectedStorageSessionLease session) =>
        Path.Combine(Path.GetFullPath(session.DataRootPath), "Archive");

    public static SqliteConnection OpenCurrent(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory connectionFactory,
        SqliteOpenMode mode,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureActive(session);

        SqliteConnection connection = connectionFactory.Open(
            CurrentPath(session),
            session.DangerousGetMasterKeyMemory(),
            mode);
        try
        {
            EnableForeignKeys(connection);
            if (mode != SqliteOpenMode.ReadOnly)
            {
                ClipboardHistoryPurge.EnableSecureDelete(connection);
            }

            ValidateIdentity(
                connection,
                session.StorageId,
                DatabaseRole.Current,
                ProtectedStorageDatabaseService.CurrentSchemaVersion,
                expectedDatabaseId: null);
            ApplicationIdentitySqlSchema.ValidateTables(connection);
            ApplicationCapturePolicySqlSchema.ValidateTables(connection);
            ClipboardHistorySqlSchema.ValidateTables(connection);
            GlobalCapturePolicySqlSchema.ValidateTables(connection);
            CustomBinaryFormatConfigurationSqlSchema.ValidateTable(connection);
            PendingPolicyMaintenanceSqlSchema.ValidateTable(connection);
            ApplicationGroupMemberSqlSchema.ValidateTable(connection);
            ValidateForeignKeys(connection, "Current");
            token.ThrowIfCancellationRequested();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public static SqliteConnection OpenArchiveForWrite(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory connectionFactory,
        ArchiveFileName fileName,
        Guid expectedDatabaseId,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureActive(session);

        SqliteConnection connection = connectionFactory.Open(
            Path.Combine(ArchiveDirectory(session), fileName.FileName),
            session.DangerousGetMasterKeyMemory(),
            SqliteOpenMode.ReadWrite);
        try
        {
            EnableForeignKeys(connection);
            ClipboardHistoryPurge.EnableSecureDelete(connection);
            ValidateIdentity(
                connection,
                session.StorageId,
                DatabaseRole.Archive,
                ProtectedArchiveDatabaseService.ArchiveSchemaVersion,
                expectedDatabaseId);
            ApplicationIdentitySqlSchema.ValidateTables(connection);
            ClipboardHistorySqlSchema.ValidateTables(connection);
            ValidateForeignKeys(connection, fileName.FileName);
            token.ThrowIfCancellationRequested();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Canonical archive file names in ordinal order; foreign files fail closed.</summary>
    public static IReadOnlyList<ArchiveFileName> EnumerateArchives(
        ProtectedStorageSessionLease session,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string directory = ArchiveDirectory(session);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var result = new List<ArchiveFileName>();
        foreach (string path in Directory.EnumerateFiles(directory, "archive_*.db", SearchOption.TopDirectoryOnly))
        {
            token.ThrowIfCancellationRequested();
            string name = Path.GetFileName(path);
            if (!ArchiveFileName.TryParse(name, out ArchiveFileName parsed) ||
                !string.Equals(name, parsed.FileName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Archive file '{name}' does not use the canonical Clipensk file name.");
            }
            result.Add(parsed);
        }

        return result.OrderBy(static name => name.FileName, StringComparer.Ordinal).ToArray();
    }

    public static void EnsureActive(ProtectedStorageSessionLease session)
    {
        if (!session.IsActive)
        {
            throw new OperationCanceledException(session.CancellationToken);
        }
    }

    private static void ValidateIdentity(
        SqliteConnection connection,
        Guid storageId,
        DatabaseRole role,
        int schemaVersion,
        Guid? expectedDatabaseId)
    {
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT SingletonId, StorageId, DatabaseId, DatabaseRole, SchemaVersion
                FROM DatabaseIdentity;
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read() ||
                reader.GetInt64(0) != 1 ||
                !Guid.TryParseExact(reader.GetString(1), "D", out Guid persistedStorageId) ||
                persistedStorageId != storageId ||
                !Guid.TryParseExact(reader.GetString(2), "D", out Guid databaseId) ||
                databaseId == Guid.Empty ||
                (expectedDatabaseId.HasValue && databaseId != expectedDatabaseId.Value) ||
                !string.Equals(reader.GetString(3), role.ToString(), StringComparison.Ordinal) ||
                reader.GetInt32(4) != schemaVersion ||
                reader.Read())
            {
                throw new InvalidDataException(
                    $"Application group maintenance requires the exact {role} v{schemaVersion} identity.");
            }
        }

        using SqliteCommand userVersion = connection.CreateCommand();
        userVersion.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) != schemaVersion)
        {
            throw new InvalidDataException(
                $"Application group maintenance requires a matching {role} user_version.");
        }
    }

    private static void ValidateForeignKeys(SqliteConnection connection, string databaseName)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        using SqliteDataReader reader = command.ExecuteReader();
        if (reader.Read())
        {
            throw new InvalidDataException(
                $"Database '{databaseName}' contains invalid foreign-key references.");
        }
    }

    private static void EnableForeignKeys(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
    }
}
