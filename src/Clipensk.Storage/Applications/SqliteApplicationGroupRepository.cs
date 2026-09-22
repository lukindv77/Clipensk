using System.Globalization;
using Clipensk.Core.Applications;
using Clipensk.Core.Storage;
using Clipensk.Storage.Clipboard;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Applications;

/// <summary>
/// Reads application groups and the set of personally configured group roots from Current v11+
/// in one snapshot, per <c>docs/APPLICATION_GROUP_PROTOCOL.md</c> §2. Persisted data that breaks a
/// group invariant fails closed instead of being interpreted.
/// </summary>
public sealed class SqliteApplicationGroupRepository
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly string _currentDatabasePath;

    public SqliteApplicationGroupRepository(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
        _currentDatabasePath = Path.Combine(
            Path.GetFullPath(session.DataRootPath),
            "Current",
            "current.db");
    }

    public ValueTask<ApplicationGroupSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            cancellationToken);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();
        if (!_session.IsActive)
        {
            throw new OperationCanceledException(_session.CancellationToken);
        }

        using SqliteConnection connection = _connectionFactory.Open(
            _currentDatabasePath,
            _session.DangerousGetMasterKeyMemory(),
            SqliteOpenMode.ReadOnly);
        EnableForeignKeys(connection);
        ValidateCurrentDatabase(connection);
        ApplicationIdentitySqlSchema.ValidateTables(connection);
        ApplicationCapturePolicySqlSchema.ValidateTables(connection);
        ApplicationGroupMemberSqlSchema.ValidateTable(connection);

        using SqliteTransaction transaction = connection.BeginTransaction();
        ApplicationGroupSnapshot snapshot = ReadSnapshotInTransaction(connection, transaction, token);
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(snapshot);
    }

    /// <summary>
    /// Reads the snapshot inside a caller-owned transaction, so a maintenance operation that already
    /// holds the storage mutation lease sees the same state it is about to change.
    /// </summary>
    internal static ApplicationGroupSnapshot ReadSnapshotInTransaction(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(connection);
        token.ThrowIfCancellationRequested();

        var memberships = new List<ApplicationGroupMembership>();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT ApplicationId, ParentApplicationId, RetainedFromApplicationId, JoinedAtUtc
                FROM ApplicationGroupMember
                ORDER BY ApplicationId COLLATE BINARY;
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                memberships.Add(new ApplicationGroupMembership(
                    ParseApplicationId(reader.GetString(0)),
                    ParseApplicationId(reader.GetString(1)),
                    reader.IsDBNull(2) ? null : ParseApplicationId(reader.GetString(2)),
                    ParseJoinedAtUtc(reader.GetString(3))));
            }
        }

        var configured = new List<ApplicationId>();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT ApplicationId
                FROM ApplicationCapturePolicy
                ORDER BY ApplicationId COLLATE BINARY;
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                configured.Add(ParseApplicationId(reader.GetString(0)));
            }
        }

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT COUNT(*)
                FROM ApplicationIdentityAlias a
                JOIN ApplicationGroupMember m ON m.ApplicationId = a.ApplicationId
                WHERE m.RetainedFromApplicationId IS NOT NULL;
                """;
            if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
            {
                throw new InvalidDataException(
                    "A retained application identity must not have resolution aliases.");
            }
        }

        token.ThrowIfCancellationRequested();
        return new ApplicationGroupSnapshot(memberships, configured);
    }

    private void ValidateCurrentDatabase(SqliteConnection connection)
    {
        int schemaVersion;
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT StorageId, DatabaseRole, SchemaVersion
                FROM DatabaseIdentity
                WHERE SingletonId = 1;
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidDataException(
                    "Application group repository requires the expected Current database identity.");
            }

            schemaVersion = reader.GetInt32(2);
            if (!Guid.TryParse(reader.GetString(0), out Guid storageId) ||
                storageId != _session.StorageId ||
                !string.Equals(reader.GetString(1), DatabaseRole.Current.ToString(), StringComparison.Ordinal) ||
                schemaVersion < ApplicationGroupMemberSqlSchema.MinimumCurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    "Application group repository requires the expected Current database identity and schema version.");
            }
        }

        using SqliteCommand userVersion = connection.CreateCommand();
        userVersion.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) != schemaVersion)
        {
            throw new InvalidDataException(
                "Current database user_version does not match the application group schema contract.");
        }
    }

    private static void EnableForeignKeys(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
    }

    private static ApplicationId ParseApplicationId(string value)
    {
        if (!Guid.TryParseExact(value, "D", out Guid parsed) ||
            parsed == Guid.Empty ||
            !string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Application group data contains a non-canonical ApplicationId.");
        }

        return new ApplicationId(parsed);
    }

    private static DateTimeOffset ParseJoinedAtUtc(string value)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTimeOffset parsed) ||
            parsed.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                "Application group membership contains an invalid UTC join timestamp.");
        }

        return parsed;
    }
}
