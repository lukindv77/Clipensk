using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Clipensk.Core.Clipboard;
using Clipensk.Core.Storage;
using Clipensk.Storage.Applications;
using Clipensk.Storage.Databases;
using Clipensk.Storage.History;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Clipboard;

public sealed record CurrentPolicyMaintenanceResult(
    PendingPolicyMaintenanceOperation Operation,
    int DeletedPayloadCount,
    bool WasAlreadyCompleted);

internal enum CurrentPolicyMaintenanceCheckpoint
{
    MutationLeaseAcquired,
    MarkerStarted,
    GlobalPolicyPublished,
    CurrentCleanupCompleted,
    BeforeCommit,
    AfterCommit,
}

/// <summary>
/// Publishes a new global capture policy and makes Current authoritative for that policy in one
/// durable transaction. Archive cleanup, catalog rebuild, Trash collection and marker completion
/// intentionally remain separate resumable phases.
/// </summary>
public sealed class ProtectedCurrentPolicyMaintenanceService
{
    public const string OperationKind = "GlobalCapturePolicyMaintenance";

    private const int StateVersion = 1;
    private const string PendingState = "pending";
    private const string CompletedState = "completed";
    private const string InProgressState = "inProgress";

    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly string _currentDatabasePath;
    private readonly Action<CurrentPolicyMaintenanceCheckpoint>? _checkpoint;

    public ProtectedCurrentPolicyMaintenanceService(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
        : this(session, connectionFactory, checkpoint: null)
    {
    }

    internal ProtectedCurrentPolicyMaintenanceService(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory,
        Action<CurrentPolicyMaintenanceCheckpoint>? checkpoint)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
        _checkpoint = checkpoint;
        _currentDatabasePath = Path.Combine(
            Path.GetFullPath(session.DataRootPath),
            "Current",
            "current.db");
    }

    public async Task<CurrentPolicyMaintenanceResult> ApplyAsync(
        ClipboardCapturePolicy newGlobalPolicy,
        CancellationToken cancellationToken = default)
    {
        ValidateNewGlobalPolicy(newGlobalPolicy);
        string policyFingerprint = ComputePolicyFingerprint(newGlobalPolicy);

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            cancellationToken);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();

        using ProtectedStorageMutationLease mutationLease =
            await _session.AcquireMutationLeaseAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        return await Task.Run(
                () => ApplyCore(newGlobalPolicy, policyFingerprint, token),
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private CurrentPolicyMaintenanceResult ApplyCore(
        ClipboardCapturePolicy newGlobalPolicy,
        string policyFingerprint,
        CancellationToken token)
    {
        _checkpoint?.Invoke(CurrentPolicyMaintenanceCheckpoint.MutationLeaseAcquired);
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
                newGlobalPolicy,
                policyFingerprint,
                token);
        }

        ClipboardCapturePolicy existingGlobalPolicy =
            ReadGlobalPolicy(connection, transaction, token)
            ?? throw new InvalidDataException(
                "Current policy maintenance requires an existing global capture policy.");
        _ = existingGlobalPolicy;

        IReadOnlyDictionary<string, ClipboardCapturePolicy> applicationPolicies =
            ReadApplicationPolicies(connection, transaction, token);
        List<PayloadKey> disallowedPayloads = ReadDisallowedPayloads(
            connection,
            transaction,
            newGlobalPolicy,
            applicationPolicies,
            token);

        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
        PendingPolicyMaintenanceOperation operation =
            SqlitePendingPolicyMaintenanceRepository.StartInTransaction(
                connection,
                transaction,
                OperationKind,
                CreateStateJson(policyFingerprint, InProgressState),
                startedAtUtc,
                token);
        _checkpoint?.Invoke(CurrentPolicyMaintenanceCheckpoint.MarkerStarted);
        token.ThrowIfCancellationRequested();

        ReplaceGlobalPolicy(connection, transaction, newGlobalPolicy, token);
        _checkpoint?.Invoke(CurrentPolicyMaintenanceCheckpoint.GlobalPolicyPublished);
        token.ThrowIfCancellationRequested();

        DeletePayloads(connection, transaction, disallowedPayloads, token);
        _checkpoint?.Invoke(CurrentPolicyMaintenanceCheckpoint.CurrentCleanupCompleted);
        token.ThrowIfCancellationRequested();

        operation = SqlitePendingPolicyMaintenanceRepository.UpdateStateInTransaction(
            connection,
            transaction,
            operation.OperationId,
            CreateStateJson(policyFingerprint, CompletedState),
            DateTimeOffset.UtcNow,
            token);

        _checkpoint?.Invoke(CurrentPolicyMaintenanceCheckpoint.BeforeCommit);

        // This is the final cancellation boundary. Once COMMIT succeeds the marker, policy and
        // Current cleanup are one durable authoritative state and must be reported as success.
        token.ThrowIfCancellationRequested();
        transaction.Commit();

        _checkpoint?.Invoke(CurrentPolicyMaintenanceCheckpoint.AfterCommit);
        return new CurrentPolicyMaintenanceResult(
            operation,
            disallowedPayloads.Count,
            WasAlreadyCompleted: false);
    }

    private CurrentPolicyMaintenanceResult ResolveCommittedRetry(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PendingPolicyMaintenanceOperation pending,
        ClipboardCapturePolicy requestedPolicy,
        string requestedFingerprint,
        CancellationToken token)
    {
        if (!string.Equals(pending.OperationKind, OperationKind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A different pending policy-maintenance operation already exists.");
        }

        CurrentMaintenanceState state = ParseCompletedState(pending.StateJson);
        ClipboardCapturePolicy persistedPolicy =
            ReadGlobalPolicy(connection, transaction, token)
            ?? throw new InvalidDataException(
                "A committed Current policy-maintenance marker requires a global policy.");

        if (!string.Equals(
                state.PolicyFingerprint,
                requestedFingerprint,
                StringComparison.Ordinal) ||
            !PoliciesEqual(persistedPolicy, requestedPolicy))
        {
            throw new InvalidOperationException(
                "The pending policy-maintenance operation belongs to a different policy change.");
        }

        token.ThrowIfCancellationRequested();
        return new CurrentPolicyMaintenanceResult(
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
                "Current policy maintenance requires the exact Current v7 identity.");
        }

        using SqliteCommand userVersion = connection.CreateCommand();
        userVersion.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) !=
            ProtectedStorageDatabaseService.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                "Current policy maintenance requires matching Current v7 user_version.");
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
                "Current policy maintenance found invalid foreign-key data.");
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

    private static IReadOnlyDictionary<string, ClipboardCapturePolicy> ReadApplicationPolicies(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken token)
    {
        var builders = new Dictionary<string, ApplicationPolicyBuilder>(StringComparer.Ordinal);
        using (SqliteCommand policies = connection.CreateCommand())
        {
            policies.Transaction = transaction;
            policies.CommandText = """
                SELECT p.ApplicationId, p.CaptureRule, i.ApplicationId
                FROM ApplicationCapturePolicy p
                LEFT JOIN ApplicationIdentity i
                    ON i.ApplicationId = p.ApplicationId
                ORDER BY p.ApplicationId COLLATE BINARY;
                """;
            using SqliteDataReader reader = policies.ExecuteReader();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                string applicationId = ReadCanonicalApplicationId(reader, 0);
                if (reader.IsDBNull(2) ||
                    !string.Equals(applicationId, reader.GetString(2), StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Application capture policy references an invalid application identity.");
                }

                ClipboardCapturePolicyRule captureRule =
                    ParseApplicationRule(reader.GetString(1));
                if (!builders.TryAdd(
                        applicationId,
                        new ApplicationPolicyBuilder(captureRule)))
                {
                    throw new InvalidDataException(
                        "Application capture policy contains duplicate application rows.");
                }
            }
        }

        using (SqliteCommand formats = connection.CreateCommand())
        {
            formats.Transaction = transaction;
            formats.CommandText = """
                SELECT ApplicationId, FormatName, CaptureRule, MaxBytes
                FROM ApplicationFormatCapturePolicy
                ORDER BY ApplicationId COLLATE BINARY, FormatName COLLATE BINARY;
                """;
            using SqliteDataReader reader = formats.ExecuteReader();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                string applicationId = ReadCanonicalApplicationId(reader, 0);
                if (!builders.TryGetValue(applicationId, out ApplicationPolicyBuilder? builder))
                {
                    throw new InvalidDataException(
                        "Application format policy contains an orphan application row.");
                }

                string formatName = reader.GetString(1);
                if (string.IsNullOrWhiteSpace(formatName))
                {
                    throw new InvalidDataException(
                        "Application format policy contains an empty format name.");
                }

                var formatPolicy = new ClipboardFormatCapturePolicy(
                    ParseApplicationRule(reader.GetString(2)),
                    ReadMaxBytes(reader, 3, "Application format capture policy"));
                if (!builder.Formats.TryAdd(formatName, formatPolicy))
                {
                    throw new InvalidDataException(
                        "Application format policy contains duplicate BINARY format names.");
                }
            }
        }

        var result = new Dictionary<string, ClipboardCapturePolicy>(
            builders.Count,
            StringComparer.Ordinal);
        foreach ((string applicationId, ApplicationPolicyBuilder builder) in builders)
        {
            result.Add(
                applicationId,
                new ClipboardCapturePolicy(builder.CaptureRule, builder.Formats));
        }

        token.ThrowIfCancellationRequested();
        return result;
    }

    private static List<PayloadKey> ReadDisallowedPayloads(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClipboardCapturePolicy newGlobalPolicy,
        IReadOnlyDictionary<string, ClipboardCapturePolicy> applicationPolicies,
        CancellationToken token)
    {
        var evaluator = new ClipboardCapturePolicyEvaluator();
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
            ORDER BY p.EventId COLLATE BINARY, p.PayloadOrder;
            """;
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

            ClipboardCapturePolicy? applicationPolicy = null;
            if (!reader.IsDBNull(4))
            {
                string sourceApplicationId = reader.GetString(4);
                if (!IsCanonicalGuid(sourceApplicationId))
                {
                    throw new InvalidDataException(
                        "Current clipboard event contains a non-canonical source application id.");
                }
                applicationPolicies.TryGetValue(sourceApplicationId, out applicationPolicy);
            }

            ClipboardCapturePolicy effective = evaluator.Merge(
                newGlobalPolicy,
                applicationPolicy);
            bool isAllowed =
                effective.Capture == ClipboardCapturePolicyRule.Allow &&
                effective.Formats.TryGetValue(
                    formatName,
                    out ClipboardFormatCapturePolicy formatPolicy) &&
                formatPolicy.Capture == ClipboardCapturePolicyRule.Allow;

            // MaxBytes is intentionally not consulted here. It is a capture-time gate, not a
            // retroactive purge rule. PayloadKind is nevertheless validated from persisted data;
            // later Archive maintenance uses it to distinguish external references.
            if (!isAllowed)
            {
                result.Add(new PayloadKey(eventId, payloadOrder));
            }
        }

        return result;
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
                throw new InvalidDataException(
                    "Global capture policy singleton changed during Current maintenance.");
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
                INSERT INTO GlobalFormatCapturePolicy (
                    SingletonId,
                    FormatName,
                    CaptureRule,
                    MaxBytes)
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
                    "Current clipboard payload changed during policy maintenance.");
            }
        }
    }

    private static void ValidateNewGlobalPolicy(ClipboardCapturePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ValidateExplicitRule(policy.Capture, nameof(policy));
        foreach ((string formatName, ClipboardFormatCapturePolicy format) in policy.Formats)
        {
            if (string.IsNullOrWhiteSpace(formatName))
            {
                throw new ArgumentException(
                    "Global clipboard format name cannot be empty.",
                    nameof(policy));
            }
            ValidateExplicitRule(format.Capture, nameof(policy));
            if (format.MaxBytes is <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(policy),
                    "Global clipboard format MaxBytes must be positive when configured.");
            }
        }
    }

    private static void ValidateExplicitRule(
        ClipboardCapturePolicyRule rule,
        string parameterName)
    {
        if (rule is not (ClipboardCapturePolicyRule.Allow or ClipboardCapturePolicyRule.Deny))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                rule,
                "Global capture rules must be explicit Allow or Deny.");
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

    private static string ReadCanonicalApplicationId(SqliteDataReader reader, int ordinal)
    {
        if (reader.GetValue(ordinal) is not string value || !IsCanonicalGuid(value))
        {
            throw new InvalidDataException(
                "Persisted application policy contains a non-canonical application id.");
        }
        return value;
    }

    private static bool IsCanonicalGuid(string value)
    {
        return Guid.TryParseExact(value, "D", out Guid parsed) &&
            string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal);
    }

    private static bool IsKnownPayloadKind(string value) => value is
        "Text" or "Link" or "PngImage" or "CustomBinary" or "StorageItems";

    private static string ComputePolicyFingerprint(ClipboardCapturePolicy policy)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("capture", policy.Capture.ToString());
            writer.WritePropertyName("formats");
            writer.WriteStartArray();
            foreach ((string formatName, ClipboardFormatCapturePolicy format) in
                     policy.Formats.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("name", formatName);
                writer.WriteString("capture", format.Capture.ToString());
                if (format.MaxBytes.HasValue)
                {
                    writer.WriteNumber("maxBytes", format.MaxBytes.Value);
                }
                else
                {
                    writer.WriteNull("maxBytes");
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static string CreateStateJson(string policyFingerprint, string currentPhase)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", StateVersion);
            writer.WriteString("policyFingerprint", policyFingerprint);
            writer.WriteString("currentPhase", currentPhase);
            writer.WriteString("archiveExternalReferenceCleanup", PendingState);
            writer.WriteString("catalogRebuild", PendingState);
            writer.WriteString("externalTrashCollection", PendingState);
            writer.WriteString("completion", PendingState);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static CurrentMaintenanceState ParseCompletedState(string stateJson)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(stateJson);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    "Current policy-maintenance state must be a JSON object.");
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException(
                        "Current policy-maintenance state contains duplicate properties.");
                }
            }

            string[] expectedNames =
            [
                "version",
                "policyFingerprint",
                "currentPhase",
                "archiveExternalReferenceCleanup",
                "catalogRebuild",
                "externalTrashCollection",
                "completion",
            ];
            if (names.Count != expectedNames.Length || expectedNames.Any(name => !names.Contains(name)))
            {
                throw new InvalidDataException(
                    "Current policy-maintenance state has an unexpected shape.");
            }

            if (!root.GetProperty("version").TryGetInt32(out int version) ||
                version != StateVersion)
            {
                throw new InvalidDataException(
                    "Current policy-maintenance state version is unsupported.");
            }

            string fingerprint = ReadRequiredStateString(root, "policyFingerprint");
            if (fingerprint.Length != 64 ||
                !fingerprint.All(static character =>
                    character is >= '0' and <= '9' or >= 'A' and <= 'F'))
            {
                throw new InvalidDataException(
                    "Current policy-maintenance policy fingerprint is invalid.");
            }

            string currentPhase = ReadRequiredStateString(root, "currentPhase");
            if (!string.Equals(currentPhase, CompletedState, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A durable Current policy-maintenance marker must have a completed Current phase.");
            }

            ValidateContinuationState(root, "archiveExternalReferenceCleanup");
            ValidateContinuationState(root, "catalogRebuild");
            ValidateContinuationState(root, "externalTrashCollection");
            ValidateContinuationState(root, "completion");

            return new CurrentMaintenanceState(fingerprint);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Current policy-maintenance state contains invalid JSON.",
                exception);
        }
    }

    private static void ValidateContinuationState(JsonElement root, string propertyName)
    {
        string value = ReadRequiredStateString(root, propertyName);
        if (value is not (PendingState or CompletedState))
        {
            throw new InvalidDataException(
                $"Current policy-maintenance continuation state '{propertyName}' is invalid.");
        }
    }

    private static string ReadRequiredStateString(JsonElement root, string propertyName)
    {
        JsonElement value = root.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException(
                $"Current policy-maintenance state '{propertyName}' is invalid.");
        }
        return value.GetString()!;
    }

    private static bool PoliciesEqual(
        ClipboardCapturePolicy left,
        ClipboardCapturePolicy right)
    {
        if (left.Capture != right.Capture || left.Formats.Count != right.Formats.Count)
        {
            return false;
        }

        foreach ((string formatName, ClipboardFormatCapturePolicy format) in left.Formats)
        {
            if (!right.Formats.TryGetValue(formatName, out ClipboardFormatCapturePolicy other) ||
                format != other)
            {
                return false;
            }
        }
        return true;
    }

    private sealed class ApplicationPolicyBuilder(
        ClipboardCapturePolicyRule captureRule)
    {
        public ClipboardCapturePolicyRule CaptureRule { get; } = captureRule;

        public Dictionary<string, ClipboardFormatCapturePolicy> Formats { get; } =
            new(StringComparer.Ordinal);
    }

    private sealed record PayloadKey(string EventId, long PayloadOrder);

    private sealed record CurrentMaintenanceState(string PolicyFingerprint);
}
