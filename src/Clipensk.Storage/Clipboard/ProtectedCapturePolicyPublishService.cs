using System.Globalization;
using Clipensk.Core.Applications;
using Clipensk.Core.Clipboard;
using Clipensk.Core.Storage;
using Clipensk.Storage.Applications;
using Clipensk.Storage.Databases;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Clipboard;

/// <summary>
/// Publishes settings changes that never touch saved history, per
/// <c>docs/APPLICATION_GROUP_PROTOCOL.md</c> §3 and §6–7: an edited global policy (the default
/// group's), an edited user group policy, a group rename and the deletion of an empty group. Each
/// is one Current transaction under the mutation lease; none creates a maintenance marker, and all
/// refuse to run while one is pending so they cannot interleave with an unfinished operation.
/// Moving an application into a group is not an edit: it purges history and goes through its own
/// maintenance operation.
/// </summary>
public sealed class ApplicationGroupNameTakenException : InvalidOperationException
{
    public ApplicationGroupNameTakenException()
        : base("Another application group already has this name.")
    {
    }
}

public sealed class ProtectedCapturePolicyPublishService
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly string _currentDatabasePath;

    public ProtectedCapturePolicyPublishService(
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

    public async Task PublishGlobalPolicyAsync(
        ClipboardCapturePolicy policy,
        CancellationToken cancellationToken = default)
    {
        CapturePolicySql.ValidateGlobalPolicy(policy);
        await RunAsync(
                (connection, transaction, token) =>
                {
                    if (!GlobalPolicyExists(connection, transaction))
                    {
                        throw new InvalidOperationException(
                            "Editing the global capture policy requires the initial setup to be saved first.");
                    }

                    CapturePolicySql.ReplaceGlobalPolicyInTransaction(connection, transaction, policy, token);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task PublishGroupPolicyAsync(
        ApplicationGroupId groupId,
        ClipboardCapturePolicy policy,
        IReadOnlyList<ApplicationCustomBinaryFormatConfiguration>? customBinaryConfigurations = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        ApplicationGroup.RequireStandalonePolicy(policy);
        Dictionary<string, string> requestedMappings = CapturePolicySql.NormalizeCustomBinaryConfigurations(
            policy,
            customBinaryConfigurations ?? []);

        await RunAsync(
                (connection, transaction, token) =>
                {
                    RequireGroup(connection, transaction, groupId, token);
                    CapturePolicySql.InsertMissingCustomBinaryConfigurationsInTransaction(
                        connection,
                        transaction,
                        requestedMappings,
                        token);
                    ApplicationGroupSql.ReplaceGroupPolicyInTransaction(connection, transaction, groupId, policy, token);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task RenameGroupAsync(
        ApplicationGroupId groupId,
        ApplicationGroupName name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        ArgumentNullException.ThrowIfNull(name);
        await RunAsync(
                (connection, transaction, token) =>
                {
                    RequireGroup(connection, transaction, groupId, token);
                    ApplicationGroupDirectory directory =
                        ApplicationGroupSql.ReadDirectoryInTransaction(connection, transaction, token);
                    if (directory.IsNameTaken(name, except: groupId))
                    {
                        throw new ApplicationGroupNameTakenException();
                    }

                    ApplicationGroupSql.RenameGroupInTransaction(connection, transaction, groupId, name);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task DeleteEmptyGroupAsync(
        ApplicationGroupId groupId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        await RunAsync(
                (connection, transaction, _) =>
                    ApplicationGroupSql.DeleteEmptyGroupInTransaction(connection, transaction, groupId),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RunAsync(
        Action<SqliteConnection, SqliteTransaction, CancellationToken> publish,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            cancellationToken);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();

        using ProtectedStorageMutationLease mutationLease =
            await _session.AcquireMutationLeaseAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        await Task.Run(
                () =>
                {
                    using SqliteConnection connection = OpenValidatedCurrent(token);
                    using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
                    if (SqlitePendingPolicyMaintenanceRepository.ReadInTransaction(
                            connection,
                            transaction,
                            token) is not null)
                    {
                        throw new PendingPolicyMaintenanceException();
                    }

                    publish(connection, transaction, token);

                    // Final cancellation boundary: a committed publication is reported as success.
                    token.ThrowIfCancellationRequested();
                    transaction.Commit();
                },
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private SqliteConnection OpenValidatedCurrent(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_session.IsActive)
        {
            throw new OperationCanceledException(_session.CancellationToken);
        }

        SqliteConnection connection = _connectionFactory.Open(
            _currentDatabasePath,
            _session.DangerousGetMasterKeyMemory(),
            SqliteOpenMode.ReadWrite);
        try
        {
            using (SqliteCommand foreignKeys = connection.CreateCommand())
            {
                foreignKeys.CommandText = "PRAGMA foreign_keys = ON;";
                foreignKeys.ExecuteNonQuery();
            }

            ValidateCurrentIdentity(connection);
            ApplicationIdentitySqlSchema.ValidateTables(connection);
            GlobalCapturePolicySqlSchema.ValidateTables(connection);
            CustomBinaryFormatConfigurationSqlSchema.ValidateTable(connection);
            PendingPolicyMaintenanceSqlSchema.ValidateTable(connection);
            ApplicationGroupSqlSchema.ValidateTables(connection);
            token.ThrowIfCancellationRequested();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void ValidateCurrentIdentity(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT SingletonId, StorageId, DatabaseRole, SchemaVersion
            FROM DatabaseIdentity;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read() ||
            reader.GetInt64(0) != 1 ||
            !Guid.TryParseExact(reader.GetString(1), "D", out Guid storageId) ||
            storageId != _session.StorageId ||
            !string.Equals(reader.GetString(2), DatabaseRole.Current.ToString(), StringComparison.Ordinal) ||
            reader.GetInt32(3) != ProtectedStorageDatabaseService.CurrentSchemaVersion ||
            reader.Read())
        {
            throw new InvalidDataException(
                "Capture policy publication requires the exact Current schema identity.");
        }

        using SqliteCommand userVersion = connection.CreateCommand();
        userVersion.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) !=
            ProtectedStorageDatabaseService.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                "Capture policy publication requires a matching Current user_version.");
        }
    }

    private static bool GlobalPolicyExists(SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM GlobalCapturePolicy WHERE SingletonId = 1;";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static void RequireGroup(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ApplicationGroupId groupId,
        CancellationToken token)
    {
        if (ApplicationGroupSql.ReadGroupInTransaction(connection, transaction, groupId, token) is null)
        {
            throw new InvalidOperationException("The application group does not exist.");
        }
    }
}
