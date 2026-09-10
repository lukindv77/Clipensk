using System.Globalization;
using Clipensk.Core.Applications;
using Clipensk.Core.Clipboard;
using Clipensk.Core.Storage;
using Clipensk.Storage.Applications;
using Clipensk.Storage.Databases;
using Clipensk.Storage.History;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Clipboard;

public sealed record CurrentApplicationPolicyMaintenanceResult(
    PendingPolicyMaintenanceOperation Operation,
    int DeletedPayloadCount,
    bool WasAlreadyCompleted);

internal enum CurrentApplicationPolicyMaintenanceCheckpoint
{
    MutationLeaseAcquired,
    MarkerStarted,
    ApplicationPolicyPublished,
    CurrentCleanupCompleted,
    BeforeCommit,
    AfterCommit,
}

/// <summary>
/// Publishes one application capture-policy override and makes Current authoritative for that
/// override in one durable transaction. Archive external-reference cleanup, catalog rebuild,
/// Trash collection and marker completion intentionally remain separate resumable phases.
/// </summary>
public sealed class ProtectedCurrentApplicationPolicyMaintenanceService
{
    public const string OperationKind = "ApplicationCapturePolicyMaintenance";

    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly string _currentDatabasePath;
    private readonly Action<CurrentApplicationPolicyMaintenanceCheckpoint>? _checkpoint;

    public ProtectedCurrentApplicationPolicyMaintenanceService(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
        : this(session, connectionFactory, checkpoint: null)
    {
    }

    internal ProtectedCurrentApplicationPolicyMaintenanceService(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory,
        Action<CurrentApplicationPolicyMaintenanceCheckpoint>? checkpoint)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
        _checkpoint = checkpoint;
        _currentDatabasePath = Path.Combine(
            Path.GetFullPath(session.DataRootPath),
            "Current",
            "current.db");
    }

    public async Task<CurrentApplicationPolicyMaintenanceResult> ApplyAsync(
        ApplicationId applicationId,
        ClipboardCapturePolicy newApplicationPolicy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(applicationId);
        ValidateApplicationPolicy(newApplicationPolicy);
        ApplicationPolicyMaintenanceState state =
            ApplicationPolicyMaintenanceStateCodec.CreateCurrentCompleted(
                applicationId,
                newApplicationPolicy);

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            cancellationToken);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();

        using ProtectedStorageMutationLease mutationLease =
            await _session.AcquireMutationLeaseAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        return await Task.Run(
                () => ApplyCore(applicationId, newApplicationPolicy, state, token),
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private CurrentApplicationPolicyMaintenanceResult ApplyCore(
        ApplicationId applicationId,
        ClipboardCapturePolicy newApplicationPolicy,
        ApplicationPolicyMaintenanceState state,
        CancellationToken token)
    {
        _checkpoint?.Invoke(CurrentApplicationPolicyMaintenanceCheckpoint.MutationLeaseAcquired);
        token.ThrowIfCancellationRequested();

        using SqliteConnection connection = OpenValidatedCurrent(token);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        PendingPolicyMaintenanceOperation? pending =
            SqlitePendingPolicyMaintenanceRepository.ReadInTransaction(
                connection,
                transaction,
                token);
        if (pending is not null)
        {
            return ResolveCommittedRetry(
                connection,
                transaction,
                pending,
                applicationId,
                newApplicationPolicy,
                state,
                token);
        }

        ClipboardCapturePolicy globalPolicy =
            ReadGlobalPolicy(connection, transaction, token)
            ?? throw new InvalidDataException(
                "Application policy maintenance requires an existing global capture policy.");
        ValidateApplicationIdentity(connection, transaction, applicationId, token);

        List<PayloadKey> disallowedPayloads = ReadDisallowedPayloads(
            connection,
            transaction,
            applicationId,
            globalPolicy,
            newApplicationPolicy,
            token);

        PendingPolicyMaintenanceOperation operation =
            SqlitePendingPolicyMaintenanceRepository.StartInTransaction(
                connection,
                transaction,
                OperationKind,
                ApplicationPolicyMaintenanceStateCodec.Serialize(state),
                DateTimeOffset.UtcNow,
                token);
        _checkpoint?.Invoke(CurrentApplicationPolicyMaintenanceCheckpoint.MarkerStarted);
        token.ThrowIfCancellationRequested();

        ReplaceApplicationPolicy(
            connection,
            transaction,
            applicationId,
            newApplicationPolicy,
            token);
        _checkpoint?.Invoke(CurrentApplicationPolicyMaintenanceCheckpoint.ApplicationPolicyPublished);
        token.ThrowIfCancellationRequested();

        DeletePayloads(connection, transaction, disallowedPayloads, token);
        _checkpoint?.Invoke(CurrentApplicationPolicyMaintenanceCheckpoint.CurrentCleanupCompleted);

        _checkpoint?.Invoke(CurrentApplicationPolicyMaintenanceCheckpoint.BeforeCommit);

        // Once COMMIT succeeds, marker, application policy and Current cleanup are one durable
        // authoritative state. Late cancellation must not demote that success.
        token.ThrowIfCancellationRequested();
        transaction.Commit();

        _checkpoint?.Invoke(CurrentApplicationPolicyMaintenanceCheckpoint.AfterCommit);
        return new CurrentApplicationPolicyMaintenanceResult(
            operation,
            disallowedPayloads.Count,
            WasAlreadyCompleted: false);
    }

    private CurrentApplicationPolicyMaintenanceResult ResolveCommittedRetry(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PendingPolicyMaintenanceOperation pending,
        ApplicationId requestedApplicationId,
        ClipboardCapturePolicy requestedPolicy,
        ApplicationPolicyMaintenanceState requestedState,
        CancellationToken token)
    {
        if (!string.Equals(pending.OperationKind, OperationKind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A different pending policy-maintenance operation already exists.");
        }

        ApplicationPolicyMaintenanceState persistedState =
            ApplicationPolicyMaintenanceStateCodec.Parse(pending.StateJson);
        if (!string.Equals(
                persistedState.ApplicationId,
                requestedApplicationId.ToString(),
                StringComparison.Ordinal) ||
            !string.Equals(
                persistedState.PolicyFingerprint,
                requestedState.PolicyFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The pending application policy-maintenance operation belongs to a different policy change.");
        }

        ClipboardCapturePolicy persistedPolicy =
            ReadApplicationPolicy(
                connection,
                transaction,
                requestedApplicationId,
                token)
            ?? throw new InvalidDataException(
                "A committed application policy-maintenance marker requires the target application policy.");
        if (!PoliciesEqual(persistedPolicy, requestedPolicy))
        {
            throw new InvalidOperationException(
                "The persisted application policy does not match the pending maintenance operation.");
        }

        token.ThrowIfCancellationRequested();
        return new CurrentApplicationPolicyMaintenanceResult(
            pending,
            DeletedPayloadCount: 0,
            WasAlreadyCompleted: true);
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
            token.ThrowIfCancellationRequested();
            using (SqliteCommand foreignKeys = connection.CreateCommand())
            {
                foreignKeys.CommandText = "PRAGMA foreign_keys = ON;";
                foreignKeys.ExecuteNonQuery();
            }

            ValidateCurrentIdentity(connection);
            ApplicationIdentitySqlSchema.ValidateTables(connection);
            ApplicationCapturePolicySqlSchema.ValidateTables(connection);
            ClipboardHistorySqlSchema.ValidateTables(connection);
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
            !string.Equals(
                reader.GetString(2),
                DatabaseRole.Current.ToString(),
                StringComparison.Ordinal) ||
            reader.GetInt32(3) != ProtectedStorageDatabaseService.CurrentSchemaVersion ||
            reader.Read())
        {
            throw new InvalidDataException(
                "Application policy maintenance requires the exact Current v7 identity.");
        }

        using SqliteCommand userVersion = connection.CreateCommand();
        userVersion.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) !=
            ProtectedStorageDatabaseService.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                "Application policy maintenance requires matching Current v7 user_version.");
        }
    }

    private static void ValidateForeignKeys(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        using SqliteDataReader reader = command.ExecuteReader();
        if (reader.Read())
        {
            throw new InvalidDataException(
                "Application policy maintenance found invalid foreign-key data.");
        }
    }

    private static void ValidateApplicationIdentity(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ApplicationId applicationId,
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
            throw new InvalidOperationException(
                "Application policy maintenance requires an existing canonical application identity.");
        }
    }

    private static ClipboardCapturePolicy? ReadGlobalPolicy(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT p.SingletonId, p.CaptureRule, f.FormatName, f.CaptureRule, f.MaxBytes
            FROM GlobalCapturePolicy p
            LEFT JOIN GlobalFormatCapturePolicy f ON f.SingletonId = p.SingletonId
            UNION ALL
            SELECT NULL, NULL, f.FormatName, f.CaptureRule, f.MaxBytes
            FROM GlobalFormatCapturePolicy f
            WHERE NOT EXISTS (
                SELECT 1
                FROM GlobalCapturePolicy p
                WHERE p.SingletonId = f.SingletonId);
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        ClipboardCapturePolicyRule? capture = null;
        var formats = new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal);
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.IsDBNull(0) ||
                reader.GetValue(0) is not long singletonId ||
                singletonId != 1 ||
                reader.GetValue(1) is not string captureText)
            {
                throw new InvalidDataException(
                    "Global capture policy contains invalid or orphan rows.");
            }

            ClipboardCapturePolicyRule captureRule = ParseExplicitGlobalRule(captureText);
            if (capture.HasValue && capture.Value != captureRule)
            {
                throw new InvalidDataException(
                    "Global capture policy contains conflicting singleton rows.");
            }
            capture = captureRule;

            if (reader.IsDBNull(2))
            {
                continue;
            }

            if (reader.GetValue(2) is not string formatName ||
                string.IsNullOrWhiteSpace(formatName) ||
                reader.GetValue(3) is not string formatRuleText)
            {
                throw new InvalidDataException(
                    "Global format capture policy contains invalid metadata.");
            }

            long? maxBytes = ReadMaxBytes(reader, 4, "Global format capture policy");
            if (!formats.TryAdd(
                    formatName,
                    new ClipboardFormatCapturePolicy(
                        ParseExplicitGlobalRule(formatRuleText),
                        maxBytes)))
            {
                throw new InvalidDataException(
                    "Global capture policy contains duplicate BINARY format names.");
            }
        }

        token.ThrowIfCancellationRequested();
        return capture.HasValue
            ? new ClipboardCapturePolicy(capture.Value, formats)
            : null;
    }

    private static ClipboardCapturePolicy? ReadApplicationPolicy(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ApplicationId applicationId,
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

    private static List<PayloadKey> ReadDisallowedPayloads(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ApplicationId applicationId,
        ClipboardCapturePolicy globalPolicy,
        ClipboardCapturePolicy newApplicationPolicy,
        CancellationToken token)
    {
        ClipboardCapturePolicy effective = new ClipboardCapturePolicyEvaluator().Merge(
            globalPolicy,
            newApplicationPolicy);
        var result = new List<PayloadKey>();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT p.EventId,
                   p.PayloadOrder,
                   p.FormatName,
                   p.PayloadKind,
                   e.SourceApplicationId
            FROM ClipboardHistoryPayload p
            JOIN ClipboardHistoryEvent e ON e.EventId = p.EventId
            WHERE e.SourceApplicationId = $applicationId COLLATE BINARY
            ORDER BY p.EventId COLLATE BINARY, p.PayloadOrder;
            """;
        command.Parameters.AddWithValue("$applicationId", applicationId.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            string eventId = reader.GetString(0);
            if (!IsCanonicalGuid(eventId))
            {
                throw new InvalidDataException(
                    "Current clipboard payload contains a non-canonical event id.");
            }

            long payloadOrder = reader.GetInt64(1);
            if (payloadOrder < 0)
            {
                throw new InvalidDataException(
                    "Current clipboard payload contains an invalid payload order.");
            }

            string formatName = reader.GetString(2);
            if (string.IsNullOrWhiteSpace(formatName))
            {
                throw new InvalidDataException(
                    "Current clipboard payload contains an empty format name.");
            }

            string payloadKind = reader.GetString(3);
            if (!IsKnownPayloadKind(payloadKind))
            {
                throw new InvalidDataException(
                    "Current clipboard payload contains an unknown persisted payload kind.");
            }

            string sourceApplicationId = reader.GetString(4);
            if (!string.Equals(
                    sourceApplicationId,
                    applicationId.ToString(),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Current clipboard event contains a non-canonical source application id.");
            }

            bool isAllowed =
                effective.Capture == ClipboardCapturePolicyRule.Allow &&
                effective.Formats.TryGetValue(
                    formatName,
                    out ClipboardFormatCapturePolicy formatPolicy) &&
                formatPolicy.Capture == ClipboardCapturePolicyRule.Allow;

            // MaxBytes remains a capture-time gate and is intentionally not a retroactive purge
            // criterion. Archive continuation will use persisted PayloadKind for external refs.
            if (!isAllowed)
            {
                result.Add(new PayloadKey(eventId, payloadOrder));
            }
        }

        return result;
    }

    private static void ReplaceApplicationPolicy(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ApplicationId applicationId,
        ClipboardCapturePolicy policy,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using (SqliteCommand upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO ApplicationCapturePolicy (ApplicationId, CaptureRule)
                VALUES ($applicationId, $captureRule)
                ON CONFLICT(ApplicationId) DO UPDATE SET
                    CaptureRule = excluded.CaptureRule;
                """;
            upsert.Parameters.AddWithValue("$applicationId", applicationId.ToString());
            upsert.Parameters.AddWithValue("$captureRule", policy.Capture.ToString());
            upsert.ExecuteNonQuery();
        }

        using (SqliteCommand deleteFormats = connection.CreateCommand())
        {
            deleteFormats.Transaction = transaction;
            deleteFormats.CommandText = """
                DELETE FROM ApplicationFormatCapturePolicy
                WHERE ApplicationId = $applicationId COLLATE BINARY;
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
                INSERT INTO ApplicationFormatCapturePolicy (
                    ApplicationId,
                    FormatName,
                    CaptureRule,
                    MaxBytes)
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

    private static void DeletePayloads(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<PayloadKey> payloads,
        CancellationToken token)
    {
        foreach (PayloadKey payload in payloads)
        {
            token.ThrowIfCancellationRequested();
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM ClipboardHistoryPayload
                WHERE EventId = $eventId COLLATE BINARY
                  AND PayloadOrder = $payloadOrder;
                """;
            command.Parameters.AddWithValue("$eventId", payload.EventId);
            command.Parameters.AddWithValue("$payloadOrder", payload.PayloadOrder);
            if (command.ExecuteNonQuery() != 1)
            {
                throw new InvalidDataException(
                    "Current clipboard payload changed during application policy maintenance.");
            }
        }
    }

    private static void ValidateApplicationPolicy(ClipboardCapturePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ValidateApplicationRule(policy.Capture, nameof(policy));
        foreach ((string formatName, ClipboardFormatCapturePolicy format) in policy.Formats)
        {
            if (string.IsNullOrWhiteSpace(formatName))
            {
                throw new ArgumentException(
                    "Application clipboard format name cannot be empty.",
                    nameof(policy));
            }
            ValidateApplicationRule(format.Capture, nameof(policy));
            if (format.MaxBytes is <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(policy),
                    "Application clipboard format MaxBytes must be positive when configured.");
            }
        }
    }

    private static void ValidateApplicationRule(
        ClipboardCapturePolicyRule rule,
        string parameterName)
    {
        if (!Enum.IsDefined(rule))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                rule,
                "Application capture policy contains an unknown rule.");
        }
    }

    private static ClipboardCapturePolicyRule ParseExplicitGlobalRule(string value) => value switch
    {
        "Allow" => ClipboardCapturePolicyRule.Allow,
        "Deny" => ClipboardCapturePolicyRule.Deny,
        _ => throw new InvalidDataException(
            "Persisted global capture rules must be explicit Allow or Deny."),
    };

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

    private static bool IsCanonicalGuid(string value)
    {
        return Guid.TryParseExact(value, "D", out Guid parsed) &&
            string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal);
    }

    private static bool IsKnownPayloadKind(string value) => value is
        "Text" or "Link" or "PngImage" or "CustomBinary" or "StorageItems";

    private static bool PoliciesEqual(
        ClipboardCapturePolicy left,
        ClipboardCapturePolicy right)
    {
        if (left.Capture != right.Capture || left.Formats.Count != right.Formats.Count)
        {
            return false;
        }
        foreach ((string formatName, ClipboardFormatCapturePolicy leftFormat) in left.Formats)
        {
            if (!right.Formats.TryGetValue(formatName, out ClipboardFormatCapturePolicy rightFormat) ||
                leftFormat != rightFormat)
            {
                return false;
            }
        }
        return true;
    }

    private sealed record PayloadKey(string EventId, long PayloadOrder);
}
