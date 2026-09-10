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

public sealed record ExternalPayloadCatalogApplicationPolicyMaintenanceResult(
    PendingPolicyMaintenanceOperation Operation,
    int CatalogAddressCount,
    bool WasAlreadyCompleted);

internal enum ExternalPayloadCatalogApplicationPolicyMaintenanceCheckpoint
{
    RebuildCompleted,
    BeforeMarkerCommit,
    AfterMarkerCommit,
}

/// <summary>
/// Continues one committed application capture-policy maintenance operation by rebuilding the
/// storage-wide ExternalPayloadAddressIndex projection and then durably marking only that
/// application's Catalog continuation phase completed. Physical Trash collection and final
/// marker completion remain later resumable phases.
/// </summary>
public sealed class ProtectedExternalPayloadCatalogApplicationPolicyMaintenanceService
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly string _currentDatabasePath;
    private readonly Action<ExternalPayloadCatalogApplicationPolicyMaintenanceCheckpoint>? _checkpoint;

    public ProtectedExternalPayloadCatalogApplicationPolicyMaintenanceService(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
        : this(session, connectionFactory, checkpoint: null)
    {
    }

    internal ProtectedExternalPayloadCatalogApplicationPolicyMaintenanceService(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory,
        Action<ExternalPayloadCatalogApplicationPolicyMaintenanceCheckpoint>? checkpoint)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
        _checkpoint = checkpoint;
        _currentDatabasePath = Path.Combine(
            Path.GetFullPath(session.DataRootPath),
            "Current",
            "current.db");
    }

    public async Task<ExternalPayloadCatalogApplicationPolicyMaintenanceResult> ApplyAsync(
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            cancellationToken);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();

        CurrentSnapshot snapshot = await Task.Run(
                () => ReadCurrentSnapshot(token),
                CancellationToken.None)
            .ConfigureAwait(false);

        if (snapshot.State.CatalogRebuild == ApplicationPolicyMaintenanceState.Completed)
        {
            token.ThrowIfCancellationRequested();
            return new ExternalPayloadCatalogApplicationPolicyMaintenanceResult(
                snapshot.Operation,
                CatalogAddressCount: 0,
                WasAlreadyCompleted: true);
        }

        if (snapshot.State.ArchiveExternalReferenceCleanup !=
            ApplicationPolicyMaintenanceState.Completed)
        {
            throw new InvalidOperationException(
                "Application Catalog maintenance requires completed Archive external-reference cleanup.");
        }

        var rebuild = new ProtectedExternalPayloadCatalogRebuildService(
            _session,
            _connectionFactory);
        IReadOnlyList<ExternalPayloadAddress> addresses =
            await rebuild.RebuildAsync(token).ConfigureAwait(false);

        // The rebuild is storage-wide because the Catalog is a storage-wide projection. Its own
        // mutation lease makes the projection commit safe. The marker publication below acquires a
        // fresh mutation lease and revalidates the exact application policy before claiming this
        // continuation phase as durable.
        _checkpoint?.Invoke(
            ExternalPayloadCatalogApplicationPolicyMaintenanceCheckpoint.RebuildCompleted);
        token.ThrowIfCancellationRequested();

        PendingPolicyMaintenanceOperation completed =
            await MarkCatalogPhaseCompletedAsync(snapshot, token).ConfigureAwait(false);

        // No cancellation check after the marker COMMIT. Durable completion of this continuation
        // phase must remain a successful result even if cancellation arrives immediately after it.
        return new ExternalPayloadCatalogApplicationPolicyMaintenanceResult(
            completed,
            addresses.Count,
            WasAlreadyCompleted: false);
    }

    private CurrentSnapshot ReadCurrentSnapshot(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using SqliteConnection connection = OpenValidatedCurrent(SqliteOpenMode.ReadOnly, token);
        using SqliteTransaction transaction = connection.BeginTransaction();

        PendingPolicyMaintenanceOperation operation =
            SqlitePendingPolicyMaintenanceRepository.ReadInTransaction(
                connection,
                transaction,
                token)
            ?? throw new InvalidOperationException(
                "Application Catalog maintenance requires a pending application policy-maintenance operation.");
        if (!string.Equals(
                operation.OperationKind,
                ProtectedCurrentApplicationPolicyMaintenanceService.OperationKind,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The pending policy-maintenance operation is not an application capture-policy change.");
        }

        ApplicationPolicyMaintenanceState state =
            ApplicationPolicyMaintenanceStateCodec.Parse(operation.StateJson);
        DurableApplicationId applicationId = ParseApplicationId(state.ApplicationId);
        ValidateApplicationIdentity(connection, transaction, applicationId, token);

        ClipboardCapturePolicy applicationPolicy =
            ReadApplicationPolicy(connection, transaction, applicationId, token)
            ?? throw new InvalidDataException(
                "Application Catalog maintenance requires the committed target application policy.");
        if (!string.Equals(
                ApplicationPolicyMaintenanceStateCodec.ComputePolicyFingerprint(applicationPolicy),
                state.PolicyFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The committed application capture policy does not match its maintenance marker.");
        }

        token.ThrowIfCancellationRequested();
        return new CurrentSnapshot(operation, state, applicationId);
    }

    private async Task<PendingPolicyMaintenanceOperation> MarkCatalogPhaseCompletedAsync(
        CurrentSnapshot snapshot,
        CancellationToken token)
    {
        using ProtectedStorageMutationLease mutationLease =
            await _session.AcquireMutationLeaseAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        return await Task.Run(
                () => MarkCatalogPhaseCompletedCore(snapshot, token),
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private PendingPolicyMaintenanceOperation MarkCatalogPhaseCompletedCore(
        CurrentSnapshot snapshot,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using SqliteConnection connection = OpenValidatedCurrent(SqliteOpenMode.ReadWrite, token);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        PendingPolicyMaintenanceOperation current =
            SqlitePendingPolicyMaintenanceRepository.ReadInTransaction(
                connection,
                transaction,
                token)
            ?? throw new InvalidOperationException(
                "The pending application policy-maintenance operation disappeared before Catalog completion.");
        if (current.OperationId != snapshot.Operation.OperationId ||
            !string.Equals(
                current.OperationKind,
                ProtectedCurrentApplicationPolicyMaintenanceService.OperationKind,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The pending policy-maintenance operation changed before application Catalog completion.");
        }

        ApplicationPolicyMaintenanceState currentState =
            ApplicationPolicyMaintenanceStateCodec.Parse(current.StateJson);
        if (!string.Equals(
                currentState.ApplicationId,
                snapshot.State.ApplicationId,
                StringComparison.Ordinal) ||
            !string.Equals(
                currentState.PolicyFingerprint,
                snapshot.State.PolicyFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The pending application policy-maintenance target changed before Catalog completion.");
        }

        DurableApplicationId currentApplicationId =
            ParseApplicationId(currentState.ApplicationId);
        if (!string.Equals(
                currentApplicationId.ToString(),
                snapshot.ApplicationId.ToString(),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The pending application policy-maintenance application changed before Catalog completion.");
        }
        ValidateApplicationIdentity(
            connection,
            transaction,
            currentApplicationId,
            token);

        ClipboardCapturePolicy applicationPolicy =
            ReadApplicationPolicy(
                connection,
                transaction,
                currentApplicationId,
                token)
            ?? throw new InvalidDataException(
                "Application Catalog completion requires the committed target application policy.");
        if (!string.Equals(
                ApplicationPolicyMaintenanceStateCodec.ComputePolicyFingerprint(applicationPolicy),
                currentState.PolicyFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The application capture policy changed before Catalog completion.");
        }

        if (currentState.ArchiveExternalReferenceCleanup !=
            ApplicationPolicyMaintenanceState.Completed)
        {
            throw new InvalidOperationException(
                "Application Catalog completion requires completed Archive external-reference cleanup.");
        }

        if (currentState.CatalogRebuild == ApplicationPolicyMaintenanceState.Completed)
        {
            token.ThrowIfCancellationRequested();
            return current;
        }

        ApplicationPolicyMaintenanceState completedState =
            currentState with { CatalogRebuild = ApplicationPolicyMaintenanceState.Completed };
        PendingPolicyMaintenanceOperation updated =
            SqlitePendingPolicyMaintenanceRepository.UpdateStateInTransaction(
                connection,
                transaction,
                current.OperationId,
                ApplicationPolicyMaintenanceStateCodec.Serialize(completedState),
                DateTimeOffset.UtcNow,
                token);

        _checkpoint?.Invoke(
            ExternalPayloadCatalogApplicationPolicyMaintenanceCheckpoint.BeforeMarkerCommit);

        // Final cancellation boundary. The mutation lease is retained through this COMMIT, so a
        // supported application-policy writer cannot change the target policy between the exact
        // revalidation above and durable phase publication.
        token.ThrowIfCancellationRequested();
        transaction.Commit();

        _checkpoint?.Invoke(
            ExternalPayloadCatalogApplicationPolicyMaintenanceCheckpoint.AfterMarkerCommit);
        return updated;
    }

    private SqliteConnection OpenValidatedCurrent(
        SqliteOpenMode mode,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_session.IsActive)
        {
            throw new OperationCanceledException(_session.CancellationToken);
        }

        SqliteConnection connection = _connectionFactory.Open(
            _currentDatabasePath,
            _session.DangerousGetMasterKeyMemory(),
            mode);
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
            PendingPolicyMaintenanceSqlSchema.ValidateTable(connection);
            ValidateForeignKeys(connection);
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
        using (SqliteCommand identity = connection.CreateCommand())
        {
            identity.CommandText = """
                SELECT SingletonId, StorageId, DatabaseRole, SchemaVersion
                FROM DatabaseIdentity;
                """;
            using SqliteDataReader reader = identity.ExecuteReader();
            if (!reader.Read() ||
                reader.GetInt64(0) != 1 ||
                !Guid.TryParseExact(reader.GetString(1), "D", out Guid storageId) ||
                storageId != _session.StorageId ||
                !string.Equals(
                    reader.GetString(2),
                    DatabaseRole.Current.ToString(),
                    StringComparison.Ordinal) ||
                reader.GetInt32(3) != ProtectedStorageDatabaseService.CurrentSchemaVersion ||
                reader.Read())
            {
                throw new InvalidDataException(
                    "Application Catalog maintenance requires the exact Current v7 identity.");
            }
        }

        using SqliteCommand userVersion = connection.CreateCommand();
        userVersion.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) !=
            ProtectedStorageDatabaseService.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                "Application Catalog maintenance requires matching Current v7 user_version.");
        }
    }

    private static void ValidateApplicationIdentity(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DurableApplicationId applicationId,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ApplicationId
            FROM ApplicationIdentity
            WHERE ApplicationId = $applicationId COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$applicationId", applicationId.ToString());
        object? value = command.ExecuteScalar();
        if (value is not string persisted ||
            !string.Equals(persisted, applicationId.ToString(), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Application Catalog maintenance requires the exact target application identity.");
        }
    }

    private static ClipboardCapturePolicy? ReadApplicationPolicy(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DurableApplicationId applicationId,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ClipboardCapturePolicyRule captureRule;
        using (SqliteCommand header = connection.CreateCommand())
        {
            header.Transaction = transaction;
            header.CommandText = """
                SELECT CaptureRule
                FROM ApplicationCapturePolicy
                WHERE ApplicationId = $applicationId COLLATE BINARY;
                """;
            header.Parameters.AddWithValue("$applicationId", applicationId.ToString());
            object? value = header.ExecuteScalar();
            if (value is null or DBNull)
            {
                return null;
            }
            if (value is not string text)
            {
                throw new InvalidDataException(
                    "Application capture policy contains an invalid capture rule.");
            }
            captureRule = ParseApplicationRule(text);
        }

        var formats = new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT FormatName, CaptureRule, MaxBytes
            FROM ApplicationFormatCapturePolicy
            WHERE ApplicationId = $applicationId COLLATE BINARY
            ORDER BY FormatName COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$applicationId", applicationId.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            string formatName = reader.GetString(0);
            if (string.IsNullOrWhiteSpace(formatName))
            {
                throw new InvalidDataException(
                    "Application format capture policy contains an empty format name.");
            }

            var format = new ClipboardFormatCapturePolicy(
                ParseApplicationRule(reader.GetString(1)),
                ReadMaxBytes(reader, 2, "Application format capture policy"));
            if (!formats.TryAdd(formatName, format))
            {
                throw new InvalidDataException(
                    "Application capture policy contains duplicate BINARY format names.");
            }
        }

        token.ThrowIfCancellationRequested();
        return new ClipboardCapturePolicy(captureRule, formats);
    }

    private static void ValidateForeignKeys(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        using SqliteDataReader reader = command.ExecuteReader();
        if (reader.Read())
        {
            throw new InvalidDataException(
                "Application Catalog maintenance found invalid Current foreign-key data.");
        }
    }

    private static DurableApplicationId ParseApplicationId(string value)
    {
        if (!Guid.TryParseExact(value, "D", out Guid parsed) ||
            parsed == Guid.Empty ||
            !string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Application policy-maintenance marker contains an invalid application id.");
        }

        return new DurableApplicationId(parsed);
    }

    private static ClipboardCapturePolicyRule ParseApplicationRule(string value) => value switch
    {
        "Inherit" => ClipboardCapturePolicyRule.Inherit,
        "Allow" => ClipboardCapturePolicyRule.Allow,
        "Deny" => ClipboardCapturePolicyRule.Deny,
        _ => throw new InvalidDataException(
            "Persisted application capture policy contains an unknown rule."),
    };

    private static long? ReadMaxBytes(
        SqliteDataReader reader,
        int ordinal,
        string contractName)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        if (reader.GetValue(ordinal) is not long maxBytes || maxBytes <= 0)
        {
            throw new InvalidDataException(
                $"{contractName} contains an invalid MaxBytes value.");
        }

        return maxBytes;
    }

    private sealed record CurrentSnapshot(
        PendingPolicyMaintenanceOperation Operation,
        ApplicationPolicyMaintenanceState State,
        DurableApplicationId ApplicationId);
}
