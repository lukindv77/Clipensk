using System.Globalization;
using Clipensk.Core.Clipboard;
using Clipensk.Core.Storage;
using Clipensk.Storage.Applications;
using Clipensk.Storage.Databases;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using DurableApplicationId = Clipensk.Core.Applications.ApplicationId;

namespace Clipensk.Storage.Clipboard;

public sealed record ApplicationPolicyMaintenanceCompletionResult(
    PendingPolicyMaintenanceOperation Operation,
    bool WasCompletionAlreadyCompleted);

internal enum ApplicationPolicyMaintenanceCompletionCheckpoint
{
    BeforeCompletionCommit,
    AfterCompletionCommit,
    BeforeMarkerClearCommit,
    AfterMarkerClearCommit,
}

/// <summary>
/// Finalizes one committed application capture-policy maintenance operation. Completion is first
/// published durably in its own Current transaction. The pending marker is then removed in a
/// second transaction so a crash or cancellation between those commits remains unambiguously
/// resumable from a durable completed application marker.
/// </summary>
public sealed class ProtectedApplicationPolicyMaintenanceCompletionService
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly string _currentDatabasePath;
    private readonly Action<ApplicationPolicyMaintenanceCompletionCheckpoint>? _checkpoint;

    public ProtectedApplicationPolicyMaintenanceCompletionService(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
        : this(session, connectionFactory, checkpoint: null)
    {
    }

    internal ProtectedApplicationPolicyMaintenanceCompletionService(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory,
        Action<ApplicationPolicyMaintenanceCompletionCheckpoint>? checkpoint)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
        _checkpoint = checkpoint;
        _currentDatabasePath = Path.Combine(
            Path.GetFullPath(session.DataRootPath),
            "Current",
            "current.db");
    }

    public async Task<ApplicationPolicyMaintenanceCompletionResult> CompleteAsync(
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            cancellationToken);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();

        using ProtectedStorageMutationLease mutationLease =
            await _session.AcquireMutationLeaseAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        return await Task.Run(
                () => CompleteCore(ReadCurrentSnapshot(token), token),
                CancellationToken.None)
            .ConfigureAwait(false);
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
                "Application policy-maintenance completion requires a pending operation.");
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
        RequirePriorPhasesCompleted(state);
        DurableApplicationId applicationId = ParseApplicationId(state.ApplicationId);
        ValidateApplicationIdentity(connection, transaction, applicationId, token);
        ValidateApplicationPolicyFingerprint(
            connection,
            transaction,
            applicationId,
            state.PolicyFingerprint,
            token);

        token.ThrowIfCancellationRequested();
        return new CurrentSnapshot(operation, state, applicationId);
    }

    private ApplicationPolicyMaintenanceCompletionResult CompleteCore(
        CurrentSnapshot snapshot,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using SqliteConnection connection = OpenValidatedCurrent(SqliteOpenMode.ReadWrite, token);

        PendingPolicyMaintenanceOperation completedOperation = snapshot.Operation;
        bool wasCompletionAlreadyCompleted =
            snapshot.State.Completion == ApplicationPolicyMaintenanceState.Completed;

        if (!wasCompletionAlreadyCompleted)
        {
            using SqliteTransaction completionTransaction =
                connection.BeginTransaction(deferred: false);
            PendingPolicyMaintenanceOperation current =
                ReadRequiredExactOperation(
                    connection,
                    completionTransaction,
                    snapshot,
                    token);
            ApplicationPolicyMaintenanceState currentState =
                ApplicationPolicyMaintenanceStateCodec.Parse(current.StateJson);
            RequirePriorPhasesCompleted(currentState);

            if (currentState.Completion == ApplicationPolicyMaintenanceState.Completed)
            {
                completedOperation = current;
                wasCompletionAlreadyCompleted = true;
            }
            else
            {
                ApplicationPolicyMaintenanceState completedState =
                    currentState with
                    {
                        Completion = ApplicationPolicyMaintenanceState.Completed,
                    };
                completedOperation =
                    SqlitePendingPolicyMaintenanceRepository.UpdateStateInTransaction(
                        connection,
                        completionTransaction,
                        current.OperationId,
                        ApplicationPolicyMaintenanceStateCodec.Serialize(completedState),
                        DateTimeOffset.UtcNow,
                        token);

                _checkpoint?.Invoke(
                    ApplicationPolicyMaintenanceCompletionCheckpoint.BeforeCompletionCommit);
                token.ThrowIfCancellationRequested();
                completionTransaction.Commit();
                _checkpoint?.Invoke(
                    ApplicationPolicyMaintenanceCompletionCheckpoint.AfterCompletionCommit);
            }
        }

        // Cancellation after the completion COMMIT leaves the durable completed marker for retry.
        token.ThrowIfCancellationRequested();

        using SqliteTransaction clearTransaction = connection.BeginTransaction(deferred: false);
        PendingPolicyMaintenanceOperation beforeClear =
            ReadRequiredExactOperation(connection, clearTransaction, snapshot, token);
        ApplicationPolicyMaintenanceState clearState =
            ApplicationPolicyMaintenanceStateCodec.Parse(beforeClear.StateJson);
        RequirePriorPhasesCompleted(clearState);
        if (clearState.Completion != ApplicationPolicyMaintenanceState.Completed)
        {
            throw new InvalidOperationException(
                "Application policy-maintenance marker cannot be cleared before completion is durable.");
        }

        SqlitePendingPolicyMaintenanceRepository.ClearInTransaction(
            connection,
            clearTransaction,
            beforeClear.OperationId,
            token);

        _checkpoint?.Invoke(
            ApplicationPolicyMaintenanceCompletionCheckpoint.BeforeMarkerClearCommit);
        token.ThrowIfCancellationRequested();
        clearTransaction.Commit();
        _checkpoint?.Invoke(
            ApplicationPolicyMaintenanceCompletionCheckpoint.AfterMarkerClearCommit);

        // Intentionally no cancellation check after marker-clear COMMIT. The full application
        // maintenance operation is durable and late cancellation must not demote success.
        return new ApplicationPolicyMaintenanceCompletionResult(
            completedOperation,
            wasCompletionAlreadyCompleted);
    }

    private PendingPolicyMaintenanceOperation ReadRequiredExactOperation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CurrentSnapshot snapshot,
        CancellationToken token)
    {
        PendingPolicyMaintenanceOperation current =
            SqlitePendingPolicyMaintenanceRepository.ReadInTransaction(
                connection,
                transaction,
                token)
            ?? throw new InvalidOperationException(
                "The pending application policy-maintenance operation disappeared before completion.");

        if (current.OperationId != snapshot.Operation.OperationId ||
            !string.Equals(
                current.OperationKind,
                ProtectedCurrentApplicationPolicyMaintenanceService.OperationKind,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The pending policy-maintenance operation changed before application completion.");
        }

        ApplicationPolicyMaintenanceState state =
            ApplicationPolicyMaintenanceStateCodec.Parse(current.StateJson);
        if (!string.Equals(
                state.ApplicationId,
                snapshot.State.ApplicationId,
                StringComparison.Ordinal) ||
            !string.Equals(
                state.PolicyFingerprint,
                snapshot.State.PolicyFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The pending application policy-maintenance target changed before completion.");
        }

        DurableApplicationId applicationId = ParseApplicationId(state.ApplicationId);
        if (!string.Equals(
                applicationId.ToString(),
                snapshot.ApplicationId.ToString(),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The pending application policy-maintenance application changed before completion.");
        }

        ValidateApplicationIdentity(connection, transaction, applicationId, token);
        ValidateApplicationPolicyFingerprint(
            connection,
            transaction,
            applicationId,
            state.PolicyFingerprint,
            token);
        return current;
    }

    private static void RequirePriorPhasesCompleted(ApplicationPolicyMaintenanceState state)
    {
        if (state.ArchiveExternalReferenceCleanup != ApplicationPolicyMaintenanceState.Completed ||
            state.CatalogRebuild != ApplicationPolicyMaintenanceState.Completed ||
            state.ExternalTrashCollection != ApplicationPolicyMaintenanceState.Completed)
        {
            throw new InvalidOperationException(
                "Application policy-maintenance completion requires Archive, Catalog and Trash phases to be completed.");
        }
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
                    "Application policy-maintenance completion requires the exact Current v7 identity.");
            }
        }

        using SqliteCommand userVersion = connection.CreateCommand();
        userVersion.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) !=
            ProtectedStorageDatabaseService.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                "Application policy-maintenance completion requires matching Current v7 user_version.");
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
                "Application policy-maintenance completion requires the exact target application identity.");
        }
    }

    private static void ValidateApplicationPolicyFingerprint(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DurableApplicationId applicationId,
        string expectedFingerprint,
        CancellationToken token)
    {
        ClipboardCapturePolicy applicationPolicy =
            ReadApplicationPolicy(connection, transaction, applicationId, token)
            ?? throw new InvalidDataException(
                "Application policy-maintenance completion requires the committed target application policy.");
        if (!string.Equals(
                ApplicationPolicyMaintenanceStateCodec.ComputePolicyFingerprint(applicationPolicy),
                expectedFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The committed application capture policy does not match its maintenance marker.");
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
                ReadMaxBytes(reader, 2));
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
                "Application policy-maintenance completion found invalid Current foreign-key data.");
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

    private static long? ReadMaxBytes(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        if (reader.GetValue(ordinal) is not long maxBytes || maxBytes <= 0)
        {
            throw new InvalidDataException(
                "Application format capture policy contains an invalid MaxBytes value.");
        }

        return maxBytes;
    }

    private sealed record CurrentSnapshot(
        PendingPolicyMaintenanceOperation Operation,
        ApplicationPolicyMaintenanceState State,
        DurableApplicationId ApplicationId);
}
