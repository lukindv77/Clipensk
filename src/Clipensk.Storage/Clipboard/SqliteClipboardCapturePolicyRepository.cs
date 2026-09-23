using System.Globalization;
using Clipensk.Core.Applications;
using Clipensk.Core.Clipboard;
using Clipensk.Core.Storage;
using Clipensk.Storage.Applications;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using DurableApplicationId = Clipensk.Core.Applications.ApplicationId;

namespace Clipensk.Storage.Clipboard;

/// <summary>
/// Supplies capture with the global policy and, for an application in a user group, that group's
/// standalone policy (<c>docs/APPLICATION_GROUP_PROTOCOL.md</c> §1, invariant 4).
/// </summary>
public sealed class SqliteClipboardCapturePolicyRepository : IClipboardCapturePolicyRepository
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly ClipboardCapturePolicy _globalPolicy;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly string _currentDatabasePath;

    public SqliteClipboardCapturePolicyRepository(
        ProtectedStorageSessionLease session,
        ClipboardCapturePolicy globalPolicy,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _globalPolicy = globalPolicy ?? throw new ArgumentNullException(nameof(globalPolicy));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
        _currentDatabasePath = Path.Combine(
            Path.GetFullPath(session.DataRootPath),
            "Current",
            "current.db");
    }

    public ValueTask<ClipboardCapturePolicy> GetGlobalPolicyAsync(
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linkedCancellation = CreateLinkedCancellation(cancellationToken);
        linkedCancellation.Token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_globalPolicy);
    }

    public ValueTask<ClipboardCapturePolicy?> GetGroupPolicyAsync(
        DurableApplicationId applicationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(applicationId);
        using CancellationTokenSource linkedCancellation = CreateLinkedCancellation(cancellationToken);
        CancellationToken token = linkedCancellation.Token;
        token.ThrowIfCancellationRequested();

        using SqliteConnection connection = OpenValidatedCurrent(token);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        ApplicationGroup? group = ApplicationGroupSql.ReadGroupOfInTransaction(
            connection,
            transaction,
            applicationId,
            token);
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(group?.Policy);
    }

    private CancellationTokenSource CreateLinkedCancellation(CancellationToken callerToken) =>
        CancellationTokenSource.CreateLinkedTokenSource(_session.CancellationToken, callerToken);

    private SqliteConnection OpenValidatedCurrent(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_session.IsActive)
        {
            throw new OperationCanceledException(_session.CancellationToken);
        }

        SqliteConnection connection = _connectionFactory.Open(
            _currentDatabasePath,
            _session.DangerousGetMasterKeyMemory(),
            SqliteOpenMode.ReadOnly);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (SqliteCommand foreignKeys = connection.CreateCommand())
            {
                foreignKeys.CommandText = "PRAGMA foreign_keys = ON;";
                foreignKeys.ExecuteNonQuery();
            }

            ValidateCurrentDatabase(connection);
            ApplicationIdentitySqlSchema.ValidateTables(connection);
            ApplicationGroupSqlSchema.ValidateTables(connection);
            cancellationToken.ThrowIfCancellationRequested();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
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
                    "Capture policy repository requires the expected Current database identity.");
            }

            schemaVersion = reader.GetInt32(2);
            if (!Guid.TryParse(reader.GetString(0), out Guid storageId) ||
                storageId != _session.StorageId ||
                !string.Equals(reader.GetString(1), DatabaseRole.Current.ToString(), StringComparison.Ordinal) ||
                schemaVersion < ApplicationGroupSqlSchema.MinimumCurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    "Capture policy repository requires Current schema v12 or later.");
            }
        }

        using SqliteCommand userVersion = connection.CreateCommand();
        userVersion.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) != schemaVersion)
        {
            throw new InvalidDataException(
                "Current database user_version does not match the capture policy schema contract.");
        }
    }
}
