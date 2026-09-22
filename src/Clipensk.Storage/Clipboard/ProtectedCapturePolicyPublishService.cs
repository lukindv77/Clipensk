using System.Globalization;
using Clipensk.Core.Clipboard;
using Clipensk.Core.Storage;
using Clipensk.Storage.Applications;
using Clipensk.Storage.Databases;
using Clipensk.Storage.ExternalFiles;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using DurableApplicationId = Clipensk.Core.Applications.ApplicationId;

namespace Clipensk.Storage.Clipboard;

/// <summary>
/// Publishes an edited global policy, or an edited personal policy of an already configured group
/// root, in one Current transaction without touching saved history, per the 2026-09-22 decision in
/// <c>docs/APPLICATION_GROUP_PROTOCOL.md</c> §3. It never creates a maintenance marker, and it
/// refuses to run while one is pending so it cannot interleave with an unfinished operation.
/// A first personal policy for an unconfigured root is not an edit: it purges history and goes
/// through its own maintenance operation.
/// </summary>
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
        ValidateGlobalPolicy(policy);
        await RunAsync(
                (connection, transaction, token) =>
                {
                    if (!GlobalPolicyExists(connection, transaction))
                    {
                        throw new InvalidOperationException(
                            "Editing the global capture policy requires the initial setup to be saved first.");
                    }

                    ReplaceGlobalPolicy(connection, transaction, policy, token);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task PublishApplicationPolicyAsync(
        DurableApplicationId rootApplicationId,
        ClipboardCapturePolicy policy,
        IReadOnlyList<ApplicationCustomBinaryFormatConfiguration>? customBinaryConfigurations = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rootApplicationId);
        ValidateApplicationPolicy(policy);
        Dictionary<string, string> requestedMappings = NormalizeCustomBinaryConfigurations(
            policy,
            customBinaryConfigurations ?? []);

        await RunAsync(
                (connection, transaction, token) =>
                {
                    RequireConfiguredRoot(connection, transaction, rootApplicationId, token);
                    InsertMissingCustomBinaryConfigurations(
                        connection,
                        transaction,
                        requestedMappings,
                        token);
                    ReplaceApplicationPolicy(connection, transaction, rootApplicationId, policy, token);
                },
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
            ApplicationCapturePolicySqlSchema.ValidateTables(connection);
            GlobalCapturePolicySqlSchema.ValidateTables(connection);
            CustomBinaryFormatConfigurationSqlSchema.ValidateTable(connection);
            PendingPolicyMaintenanceSqlSchema.ValidateTable(connection);
            ApplicationGroupMemberSqlSchema.ValidateTable(connection);
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

    private static void RequireConfiguredRoot(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DurableApplicationId applicationId,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM ApplicationIdentity WHERE ApplicationId = $id),
                (SELECT COUNT(*) FROM ApplicationGroupMember WHERE ApplicationId = $id),
                (SELECT COUNT(*) FROM ApplicationCapturePolicy WHERE ApplicationId = $id);
            """;
        command.Parameters.AddWithValue("$id", applicationId.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        reader.Read();
        if (reader.GetInt64(0) != 1)
        {
            throw new InvalidOperationException(
                "Capture policy publication requires an existing application identity.");
        }
        if (reader.GetInt64(1) != 0)
        {
            throw new InvalidOperationException(
                "A group member cannot have its own capture policy; change the group root's policy instead.");
        }
        if (reader.GetInt64(2) != 1)
        {
            throw new InvalidOperationException(
                "A first personal policy purges history and must go through its own maintenance operation.");
        }
    }

    private static void ReplaceGlobalPolicy(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClipboardCapturePolicy policy,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using (SqliteCommand update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE GlobalCapturePolicy
                SET CaptureRule = $captureRule
                WHERE SingletonId = 1;
                """;
            update.Parameters.AddWithValue("$captureRule", policy.Capture.ToString());
            if (update.ExecuteNonQuery() != 1)
            {
                throw new InvalidDataException("Global capture policy singleton is missing.");
            }
        }

        using (SqliteCommand deleteFormats = connection.CreateCommand())
        {
            deleteFormats.Transaction = transaction;
            deleteFormats.CommandText = "DELETE FROM GlobalFormatCapturePolicy WHERE SingletonId = 1;";
            deleteFormats.ExecuteNonQuery();
        }

        foreach ((string formatName, ClipboardFormatCapturePolicy format) in
                 policy.Formats.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO GlobalFormatCapturePolicy (SingletonId, FormatName, CaptureRule, MaxBytes)
                VALUES (1, $formatName, $captureRule, $maxBytes);
                """;
            insert.Parameters.AddWithValue("$formatName", formatName);
            insert.Parameters.AddWithValue("$captureRule", format.Capture.ToString());
            insert.Parameters.AddWithValue(
                "$maxBytes",
                format.MaxBytes.HasValue ? format.MaxBytes.Value : DBNull.Value);
            insert.ExecuteNonQuery();
        }
    }

    private static void ReplaceApplicationPolicy(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DurableApplicationId applicationId,
        ClipboardCapturePolicy policy,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using (SqliteCommand update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE ApplicationCapturePolicy
                SET CaptureRule = $captureRule
                WHERE ApplicationId = $applicationId;
                """;
            update.Parameters.AddWithValue("$applicationId", applicationId.ToString());
            update.Parameters.AddWithValue("$captureRule", policy.Capture.ToString());
            if (update.ExecuteNonQuery() != 1)
            {
                throw new InvalidDataException("Application capture policy changed during publication.");
            }
        }

        using (SqliteCommand deleteFormats = connection.CreateCommand())
        {
            deleteFormats.Transaction = transaction;
            deleteFormats.CommandText = """
                DELETE FROM ApplicationFormatCapturePolicy
                WHERE ApplicationId = $applicationId;
                """;
            deleteFormats.Parameters.AddWithValue("$applicationId", applicationId.ToString());
            deleteFormats.ExecuteNonQuery();
        }

        foreach ((string formatName, ClipboardFormatCapturePolicy format) in
                 policy.Formats.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO ApplicationFormatCapturePolicy (ApplicationId, FormatName, CaptureRule, MaxBytes)
                VALUES ($applicationId, $formatName, $captureRule, $maxBytes);
                """;
            insert.Parameters.AddWithValue("$applicationId", applicationId.ToString());
            insert.Parameters.AddWithValue("$formatName", formatName);
            insert.Parameters.AddWithValue("$captureRule", format.Capture.ToString());
            insert.Parameters.AddWithValue(
                "$maxBytes",
                format.MaxBytes.HasValue ? format.MaxBytes.Value : DBNull.Value);
            insert.ExecuteNonQuery();
        }
    }

    private static void InsertMissingCustomBinaryConfigurations(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyDictionary<string, string> requested,
        CancellationToken token)
    {
        foreach ((string formatName, string fileExtension) in
                 requested.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            using (SqliteCommand read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = """
                    SELECT FileExtension
                    FROM CustomBinaryFormatConfiguration
                    WHERE FormatName = $formatName COLLATE BINARY;
                    """;
                read.Parameters.AddWithValue("$formatName", formatName);
                if (read.ExecuteScalar() is string existing)
                {
                    if (!string.Equals(existing, fileExtension, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Custom binary format '{formatName}' is already mapped to a different extension; rebind requires cleanup.");
                    }
                    continue;
                }
            }

            using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO CustomBinaryFormatConfiguration (FormatName, FileExtension)
                VALUES ($formatName, $fileExtension);
                """;
            insert.Parameters.AddWithValue("$formatName", formatName);
            insert.Parameters.AddWithValue("$fileExtension", fileExtension);
            insert.ExecuteNonQuery();
        }
    }

    private static Dictionary<string, string> NormalizeCustomBinaryConfigurations(
        ClipboardCapturePolicy policy,
        IReadOnlyList<ApplicationCustomBinaryFormatConfiguration> configurations)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ApplicationCustomBinaryFormatConfiguration configuration in configurations)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            if (string.IsNullOrWhiteSpace(configuration.FormatName) ||
                !ClipboardCaptureFormatGuard.IsCaptureAllowed(configuration.FormatName))
            {
                throw new ArgumentException(
                    $"Clipboard format '{configuration.FormatName}' cannot be mapped for capture.",
                    nameof(configurations));
            }
            if (!policy.Formats.TryGetValue(configuration.FormatName, out ClipboardFormatCapturePolicy format) ||
                format.Capture != ClipboardCapturePolicyRule.Allow)
            {
                throw new ArgumentException(
                    $"Custom binary mapping '{configuration.FormatName}' requires an explicit Allow rule.",
                    nameof(configurations));
            }

            string normalized = ExternalPayloadAddressFactory.NormalizeCustomBinaryExtension(
                configuration.FileExtension);
            if (!result.TryAdd(configuration.FormatName, normalized))
            {
                throw new ArgumentException(
                    $"Duplicate custom binary mapping '{configuration.FormatName}' is not allowed.",
                    nameof(configurations));
            }
        }
        return result;
    }

    private static void ValidateGlobalPolicy(ClipboardCapturePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.Capture is not (ClipboardCapturePolicyRule.Allow or ClipboardCapturePolicyRule.Deny))
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "Global capture rules must be explicit Allow or Deny.");
        }
        foreach ((string formatName, ClipboardFormatCapturePolicy format) in policy.Formats)
        {
            if (string.IsNullOrWhiteSpace(formatName) ||
                format.Capture is not (ClipboardCapturePolicyRule.Allow or ClipboardCapturePolicyRule.Deny) ||
                format.MaxBytes is <= 0)
            {
                throw new ArgumentException("Global capture policy contains an invalid format rule.", nameof(policy));
            }
        }
    }

    private static void ValidateApplicationPolicy(ClipboardCapturePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!Enum.IsDefined(policy.Capture))
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "Application capture policy contains an unknown rule.");
        }
        foreach ((string formatName, ClipboardFormatCapturePolicy format) in policy.Formats)
        {
            if (string.IsNullOrWhiteSpace(formatName) ||
                !Enum.IsDefined(format.Capture) ||
                format.MaxBytes is <= 0)
            {
                throw new ArgumentException("Application capture policy contains an invalid format rule.", nameof(policy));
            }
        }
    }
}
