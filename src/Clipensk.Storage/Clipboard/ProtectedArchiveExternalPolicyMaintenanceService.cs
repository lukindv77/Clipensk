using System.Globalization;
using Clipensk.Core.Clipboard;
using Clipensk.Core.Storage;
using Clipensk.Storage.Applications;
using Clipensk.Storage.Databases;
using Clipensk.Storage.History;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Clipboard;

public sealed record ArchiveExternalPolicyMaintenanceResult(
    PendingPolicyMaintenanceOperation Operation,
    int DeletedPayloadCount,
    int ArchiveDatabaseCount,
    bool WasAlreadyCompleted);

internal enum ArchiveExternalPolicyMaintenanceCheckpoint
{
    MutationLeaseAcquired,
    PreflightCompleted,
    ArchiveCommitCompleted,
    BeforeMarkerCommit,
    AfterMarkerCommit,
}

/// <summary>
/// Resumes a committed global policy-maintenance operation by removing only disallowed external
/// payload references from Archive v1 databases. Ordinary Archive DB payloads are preserved.
/// Catalog rebuild, physical Trash collection and final marker completion remain later phases.
/// </summary>
public sealed class ProtectedArchiveExternalPolicyMaintenanceService
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly string _currentDatabasePath;
    private readonly string _archiveDirectoryPath;
    private readonly string _filesRootPath;
    private readonly string _filesRootPrefix;
    private readonly Action<ArchiveExternalPolicyMaintenanceCheckpoint>? _checkpoint;

    public ProtectedArchiveExternalPolicyMaintenanceService(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
        : this(session, connectionFactory, checkpoint: null)
    {
    }

    internal ProtectedArchiveExternalPolicyMaintenanceService(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory,
        Action<ArchiveExternalPolicyMaintenanceCheckpoint>? checkpoint)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
        _checkpoint = checkpoint;

        string root = Path.GetFullPath(session.DataRootPath);
        _currentDatabasePath = Path.Combine(root, "Current", "current.db");
        _archiveDirectoryPath = Path.Combine(root, "Archive");
        _filesRootPath = Path.TrimEndingDirectorySeparator(Path.Combine(root, "Files"));
        _filesRootPrefix = _filesRootPath + Path.DirectorySeparatorChar;
    }

    public async Task<ArchiveExternalPolicyMaintenanceResult> ApplyAsync(
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            cancellationToken);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();

        using ProtectedStorageMutationLease mutationLease =
            await _session.AcquireMutationLeaseAsync(token).ConfigureAwait(false);
        _checkpoint?.Invoke(ArchiveExternalPolicyMaintenanceCheckpoint.MutationLeaseAcquired);
        token.ThrowIfCancellationRequested();

        CurrentSnapshot snapshot = await Task.Run(
                () => ReadCurrentSnapshot(token),
                CancellationToken.None)
            .ConfigureAwait(false);

        if (snapshot.State.ArchiveExternalReferenceCleanup ==
            GlobalPolicyMaintenanceState.Completed)
        {
            token.ThrowIfCancellationRequested();
            return new ArchiveExternalPolicyMaintenanceResult(
                snapshot.Operation,
                DeletedPayloadCount: 0,
                ArchiveDatabaseCount: 0,
                WasAlreadyCompleted: true);
        }

        string[] archiveNamesBefore = await Task.Run(
                () => EnumerateArchiveNames(token),
                CancellationToken.None)
            .ConfigureAwait(false);

        var archiveService = new ProtectedArchiveDatabaseService(
            _session,
            _connectionFactory);
        var plans = new List<ArchivePlan>(archiveNamesBefore.Length);
        var databaseIds = new HashSet<Guid>();

        foreach (string fileNameText in archiveNamesBefore)
        {
            token.ThrowIfCancellationRequested();
            ArchiveFileName archiveFileName = ParseArchiveFileName(fileNameText);
            DatabaseIdentity identityBefore = await archiveService
                .ValidateAsync(archiveFileName, token)
                .ConfigureAwait(false);
            if (!databaseIds.Add(identityBefore.DatabaseId))
            {
                throw new InvalidDataException(
                    "Multiple archive files expose the same DatabaseId during policy maintenance.");
            }

            ArchivePlan plan = await Task.Run(
                    () => ReadArchivePlan(
                        archiveFileName,
                        identityBefore.DatabaseId,
                        snapshot,
                        token),
                    CancellationToken.None)
                .ConfigureAwait(false);

            DatabaseIdentity identityAfter = await archiveService
                .ValidateAsync(archiveFileName, token)
                .ConfigureAwait(false);
            if (identityBefore != identityAfter)
            {
                throw new InvalidOperationException(
                    $"Archive '{archiveFileName.FileName}' identity changed during policy-maintenance preflight.");
            }

            plans.Add(plan with { Identity = identityBefore });
        }

        string[] archiveNamesAfterPreflight = await Task.Run(
                () => EnumerateArchiveNames(token),
                CancellationToken.None)
            .ConfigureAwait(false);
        EnsureArchiveSetUnchanged(archiveNamesBefore, archiveNamesAfterPreflight);

        _checkpoint?.Invoke(ArchiveExternalPolicyMaintenanceCheckpoint.PreflightCompleted);
        token.ThrowIfCancellationRequested();

        int deletedPayloadCount = 0;
        foreach (ArchivePlan plan in plans)
        {
            token.ThrowIfCancellationRequested();
            int deleted = await Task.Run(
                    () => ApplyArchivePlan(plan, token),
                    CancellationToken.None)
                .ConfigureAwait(false);
            deletedPayloadCount += deleted;

            if (deleted > 0)
            {
                DatabaseIdentity identityAfterWrite = await archiveService
                    .ValidateAsync(plan.FileName, token)
                    .ConfigureAwait(false);
                if (identityAfterWrite != plan.Identity)
                {
                    throw new InvalidOperationException(
                        $"Archive '{plan.FileName.FileName}' identity changed during policy cleanup.");
                }

                _checkpoint?.Invoke(
                    ArchiveExternalPolicyMaintenanceCheckpoint.ArchiveCommitCompleted);
                token.ThrowIfCancellationRequested();
            }
        }

        string[] archiveNamesAfterWrites = await Task.Run(
                () => EnumerateArchiveNames(token),
                CancellationToken.None)
            .ConfigureAwait(false);
        EnsureArchiveSetUnchanged(archiveNamesBefore, archiveNamesAfterWrites);

        PendingPolicyMaintenanceOperation completed = await Task.Run(
                () => MarkArchivePhaseCompleted(snapshot, token),
                CancellationToken.None)
            .ConfigureAwait(false);

        // There is intentionally no cancellation check after the marker commit. Once the
        // durable continuation state says Archive cleanup completed, late cancellation cannot
        // demote the successful phase.
        return new ArchiveExternalPolicyMaintenanceResult(
            completed,
            deletedPayloadCount,
            plans.Count,
            WasAlreadyCompleted: false);
    }

    private CurrentSnapshot ReadCurrentSnapshot(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureActiveSession();

        using SqliteConnection connection = _connectionFactory.Open(
            _currentDatabasePath,
            _session.DangerousGetMasterKeyMemory(),
            SqliteOpenMode.ReadOnly);
        EnableForeignKeys(connection);
        ValidateCurrentDatabase(connection);
        ApplicationIdentitySqlSchema.ValidateTables(connection);
        ApplicationCapturePolicySqlSchema.ValidateTables(connection);
        ClipboardHistorySqlSchema.ValidateTables(connection);
        GlobalCapturePolicySqlSchema.ValidateTables(connection);
        PendingPolicyMaintenanceSqlSchema.ValidateTable(connection);
        ValidateForeignKeys(connection, "Current");

        using SqliteTransaction transaction = connection.BeginTransaction();
        PendingPolicyMaintenanceOperation operation =
            SqlitePendingPolicyMaintenanceRepository.ReadInTransaction(
                connection,
                transaction,
                token)
            ?? throw new InvalidOperationException(
                "Archive policy maintenance requires a pending global policy-maintenance operation.");
        if (!string.Equals(
                operation.OperationKind,
                ProtectedCurrentPolicyMaintenanceService.OperationKind,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The pending policy-maintenance operation is not a global capture-policy change.");
        }

        GlobalPolicyMaintenanceState state =
            GlobalPolicyMaintenanceStateCodec.Parse(operation.StateJson);
        ClipboardCapturePolicy globalPolicy =
            ReadGlobalPolicy(connection, transaction, token)
            ?? throw new InvalidDataException(
                "Archive policy maintenance requires the committed global capture policy.");
        string persistedFingerprint =
            GlobalPolicyMaintenanceStateCodec.ComputePolicyFingerprint(globalPolicy);
        if (!string.Equals(
                persistedFingerprint,
                state.PolicyFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The committed global capture policy does not match its maintenance marker.");
        }

        IReadOnlyDictionary<string, ClipboardCapturePolicy> applicationPolicies =
            ReadApplicationPolicies(connection, transaction, token);
        token.ThrowIfCancellationRequested();
        return new CurrentSnapshot(operation, state, globalPolicy, applicationPolicies);
    }

    private ArchivePlan ReadArchivePlan(
        ArchiveFileName archiveFileName,
        Guid expectedDatabaseId,
        CurrentSnapshot snapshot,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureActiveSession();

        string databasePath = Path.Combine(
            _archiveDirectoryPath,
            archiveFileName.FileName);
        using SqliteConnection connection = _connectionFactory.Open(
            databasePath,
            _session.DangerousGetMasterKeyMemory(),
            SqliteOpenMode.ReadOnly);
        EnableForeignKeys(connection);
        ValidateArchiveHistoryDatabase(connection, expectedDatabaseId);
        ApplicationIdentitySqlSchema.ValidateTables(connection);
        ClipboardHistorySqlSchema.ValidateTables(connection);
        ValidateForeignKeys(connection, archiveFileName.FileName);

        var evaluator = new ClipboardCapturePolicyEvaluator();
        var deletions = new List<ArchivePayloadKey>();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.EventId,
                   p.PayloadOrder,
                   p.FormatName,
                   p.PayloadKind,
                   p.CanonicalByteCount,
                   p.InlineCanonicalText,
                   p.SearchText,
                   p.ExternalSha256,
                   p.ExternalRelativePath,
                   p.ExternalSizeBytes,
                   e.SourceApplicationId
            FROM ClipboardHistoryPayload p
            JOIN ClipboardHistoryEvent e ON e.EventId = p.EventId
            ORDER BY p.EventId COLLATE BINARY, p.PayloadOrder;
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            string eventId = ReadCanonicalGuid(
                reader,
                0,
                "Archive clipboard payload contains a non-canonical event id.");
            long payloadOrder = reader.GetInt64(1);
            if (payloadOrder < 0)
            {
                throw new InvalidDataException(
                    "Archive clipboard payload contains an invalid payload order.");
            }

            string formatName = reader.GetString(2);
            if (string.IsNullOrWhiteSpace(formatName))
            {
                throw new InvalidDataException(
                    "Archive clipboard payload contains an empty format name.");
            }

            string payloadKind = reader.GetString(3);
            long canonicalByteCount = reader.GetInt64(4);
            if (canonicalByteCount < 0)
            {
                throw new InvalidDataException(
                    "Archive clipboard payload contains a negative canonical byte count.");
            }

            ValidatePayloadRepresentation(
                reader,
                payloadKind,
                canonicalByteCount);

            ClipboardCapturePolicy? applicationPolicy = null;
            if (!reader.IsDBNull(10))
            {
                string sourceApplicationId = ReadCanonicalGuid(
                    reader,
                    10,
                    "Archive clipboard event contains a non-canonical source application id.");
                snapshot.ApplicationPolicies.TryGetValue(
                    sourceApplicationId,
                    out applicationPolicy);
            }

            if (payloadKind is not ("PngImage" or "CustomBinary"))
            {
                // REQUIREMENTS §18 preserves ordinary DB payloads already in Archive.
                continue;
            }

            ClipboardCapturePolicy effective = evaluator.Merge(
                snapshot.GlobalPolicy,
                applicationPolicy);
            bool isAllowed =
                effective.Capture == ClipboardCapturePolicyRule.Allow &&
                effective.Formats.TryGetValue(
                    formatName,
                    out ClipboardFormatCapturePolicy formatPolicy) &&
                formatPolicy.Capture == ClipboardCapturePolicyRule.Allow;

            // MaxBytes is capture-time only and is intentionally not a retroactive purge gate.
            // Persisted PayloadKind, not FormatName, decides whether Archive cleanup applies.
            if (!isAllowed)
            {
                deletions.Add(new ArchivePayloadKey(
                    eventId,
                    payloadOrder,
                    formatName,
                    payloadKind));
            }
        }

        token.ThrowIfCancellationRequested();
        return new ArchivePlan(
            archiveFileName,
            expectedDatabaseId,
            Identity: null!,
            deletions);
    }

    private int ApplyArchivePlan(ArchivePlan plan, CancellationToken token)
    {
        if (plan.Deletions.Count == 0)
        {
            return 0;
        }

        token.ThrowIfCancellationRequested();
        EnsureActiveSession();
        string databasePath = Path.Combine(
            _archiveDirectoryPath,
            plan.FileName.FileName);
        using SqliteConnection connection = _connectionFactory.Open(
            databasePath,
            _session.DangerousGetMasterKeyMemory(),
            SqliteOpenMode.ReadWrite);
        EnableForeignKeys(connection);
        ValidateArchiveHistoryDatabase(connection, plan.DatabaseId);
        ApplicationIdentitySqlSchema.ValidateTables(connection);
        ClipboardHistorySqlSchema.ValidateTables(connection);
        ValidateForeignKeys(connection, plan.FileName.FileName);

        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        foreach (ArchivePayloadKey payload in plan.Deletions)
        {
            token.ThrowIfCancellationRequested();
            using SqliteCommand delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM ClipboardHistoryPayload
                WHERE EventId = $eventId COLLATE BINARY
                  AND PayloadOrder = $payloadOrder
                  AND FormatName = $formatName COLLATE BINARY
                  AND PayloadKind = $payloadKind COLLATE BINARY;
                """;
            delete.Parameters.AddWithValue("$eventId", payload.EventId);
            delete.Parameters.AddWithValue("$payloadOrder", payload.PayloadOrder);
            delete.Parameters.AddWithValue("$formatName", payload.FormatName);
            delete.Parameters.AddWithValue("$payloadKind", payload.PayloadKind);
            if (delete.ExecuteNonQuery() != 1)
            {
                throw new InvalidDataException(
                    "Archive clipboard payload changed during policy maintenance.");
            }
        }

        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return plan.Deletions.Count;
    }

    private PendingPolicyMaintenanceOperation MarkArchivePhaseCompleted(
        CurrentSnapshot snapshot,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureActiveSession();

        using SqliteConnection connection = _connectionFactory.Open(
            _currentDatabasePath,
            _session.DangerousGetMasterKeyMemory(),
            SqliteOpenMode.ReadWrite);
        EnableForeignKeys(connection);
        ValidateCurrentDatabase(connection);
        ApplicationIdentitySqlSchema.ValidateTables(connection);
        ApplicationCapturePolicySqlSchema.ValidateTables(connection);
        ClipboardHistorySqlSchema.ValidateTables(connection);
        GlobalCapturePolicySqlSchema.ValidateTables(connection);
        PendingPolicyMaintenanceSqlSchema.ValidateTable(connection);
        ValidateForeignKeys(connection, "Current");

        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        PendingPolicyMaintenanceOperation current =
            SqlitePendingPolicyMaintenanceRepository.ReadInTransaction(
                connection,
                transaction,
                token)
            ?? throw new InvalidOperationException(
                "The pending policy-maintenance operation disappeared before Archive completion.");
        if (current.OperationId != snapshot.Operation.OperationId ||
            !string.Equals(
                current.OperationKind,
                ProtectedCurrentPolicyMaintenanceService.OperationKind,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The pending policy-maintenance operation changed before Archive completion.");
        }

        GlobalPolicyMaintenanceState currentState =
            GlobalPolicyMaintenanceStateCodec.Parse(current.StateJson);
        if (!string.Equals(
                currentState.PolicyFingerprint,
                snapshot.State.PolicyFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The pending policy-maintenance fingerprint changed before Archive completion.");
        }

        ClipboardCapturePolicy persistedGlobal =
            ReadGlobalPolicy(connection, transaction, token)
            ?? throw new InvalidDataException(
                "Archive completion requires the committed global capture policy.");
        if (!string.Equals(
                GlobalPolicyMaintenanceStateCodec.ComputePolicyFingerprint(persistedGlobal),
                currentState.PolicyFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The global capture policy changed before Archive completion.");
        }

        if (currentState.ArchiveExternalReferenceCleanup ==
            GlobalPolicyMaintenanceState.Completed)
        {
            token.ThrowIfCancellationRequested();
            return current;
        }

        GlobalPolicyMaintenanceState completedState =
            currentState.WithArchiveExternalReferenceCleanupCompleted();
        PendingPolicyMaintenanceOperation updated =
            SqlitePendingPolicyMaintenanceRepository.UpdateStateInTransaction(
                connection,
                transaction,
                current.OperationId,
                GlobalPolicyMaintenanceStateCodec.Serialize(completedState),
                DateTimeOffset.UtcNow,
                token);

        _checkpoint?.Invoke(ArchiveExternalPolicyMaintenanceCheckpoint.BeforeMarkerCommit);
        token.ThrowIfCancellationRequested();
        transaction.Commit();
        _checkpoint?.Invoke(ArchiveExternalPolicyMaintenanceCheckpoint.AfterMarkerCommit);
        return updated;
    }

    private string[] EnumerateArchiveNames(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Directory.Exists(_archiveDirectoryPath))
        {
            return [];
        }

        var names = new List<string>();
        foreach (string path in Directory.EnumerateFiles(
                     _archiveDirectoryPath,
                     "archive_*.db",
                     SearchOption.TopDirectoryOnly))
        {
            token.ThrowIfCancellationRequested();
            string fileName = Path.GetFileName(path);
            _ = ParseArchiveFileName(fileName);
            names.Add(fileName);
        }

        return names.OrderBy(name => name, StringComparer.Ordinal).ToArray();
    }

    private static ArchiveFileName ParseArchiveFileName(string fileName)
    {
        if (!ArchiveFileName.TryParse(fileName, out ArchiveFileName parsed) ||
            !string.Equals(fileName, parsed.FileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Archive file '{fileName}' does not use the canonical Clipensk file name.");
        }

        return parsed;
    }

    private static void EnsureArchiveSetUnchanged(
        IReadOnlyList<string> before,
        IReadOnlyList<string> after)
    {
        if (!before.SequenceEqual(after, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "Archive directory changed during policy maintenance; retry the operation.");
        }
    }

    private void ValidateCurrentDatabase(SqliteConnection connection)
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
                "Archive policy maintenance requires the exact Current v7 identity.");
        }

        using SqliteCommand userVersion = connection.CreateCommand();
        userVersion.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) !=
            ProtectedStorageDatabaseService.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                "Archive policy maintenance requires matching Current v7 user_version.");
        }
    }

    private void ValidateArchiveHistoryDatabase(
        SqliteConnection connection,
        Guid expectedDatabaseId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT SingletonId, StorageId, DatabaseId, DatabaseRole, SchemaVersion
            FROM DatabaseIdentity;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read() ||
            reader.GetInt64(0) != 1 ||
            !Guid.TryParseExact(reader.GetString(1), "D", out Guid storageId) ||
            storageId != _session.StorageId ||
            !Guid.TryParseExact(reader.GetString(2), "D", out Guid databaseId) ||
            databaseId != expectedDatabaseId ||
            databaseId == Guid.Empty ||
            !string.Equals(
                reader.GetString(3),
                DatabaseRole.Archive.ToString(),
                StringComparison.Ordinal) ||
            reader.GetInt32(4) != ProtectedArchiveDatabaseService.ArchiveSchemaVersion ||
            reader.Read())
        {
            throw new InvalidDataException(
                "Archive policy maintenance requires the exact Archive v1 identity.");
        }

        using SqliteCommand userVersion = connection.CreateCommand();
        userVersion.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) !=
            ProtectedArchiveDatabaseService.ArchiveSchemaVersion)
        {
            throw new InvalidDataException(
                "Archive policy maintenance requires matching Archive v1 user_version.");
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

            long? maxBytes = ReadMaxBytes(
                reader,
                4,
                "Global format capture policy");
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
                string applicationId = ReadCanonicalGuid(
                    reader,
                    0,
                    "Persisted application policy contains a non-canonical application id.");
                if (reader.IsDBNull(2) ||
                    !string.Equals(
                        applicationId,
                        reader.GetString(2),
                        StringComparison.Ordinal))
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
                string applicationId = ReadCanonicalGuid(
                    reader,
                    0,
                    "Persisted application format policy contains a non-canonical application id.");
                if (!builders.TryGetValue(
                        applicationId,
                        out ApplicationPolicyBuilder? builder))
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
                    ReadMaxBytes(
                        reader,
                        3,
                        "Application format capture policy"));
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

    private void ValidatePayloadRepresentation(
        SqliteDataReader reader,
        string payloadKind,
        long canonicalByteCount)
    {
        bool inlineIsNull = reader.IsDBNull(5);
        bool searchTextIsNull = reader.IsDBNull(6);
        bool externalShaIsNull = reader.IsDBNull(7);
        bool externalPathIsNull = reader.IsDBNull(8);
        bool externalSizeIsNull = reader.IsDBNull(9);

        if (payloadKind is "Text" or "Link" or "StorageItems")
        {
            if (inlineIsNull ||
                !externalShaIsNull ||
                !externalPathIsNull ||
                !externalSizeIsNull)
            {
                throw new InvalidDataException(
                    "Archive inline payload representation is invalid during policy maintenance.");
            }
            return;
        }

        if (payloadKind is not ("PngImage" or "CustomBinary"))
        {
            throw new InvalidDataException(
                "Archive contains an unsupported persisted payload kind during policy maintenance.");
        }

        if (!inlineIsNull ||
            !searchTextIsNull ||
            externalShaIsNull ||
            externalPathIsNull ||
            externalSizeIsNull)
        {
            throw new InvalidDataException(
                "Archive external payload representation is incomplete during policy maintenance.");
        }

        string sha256 = reader.GetString(7);
        string relativePath = reader.GetString(8);
        long sizeBytes = reader.GetInt64(9);
        if (sizeBytes != canonicalByteCount)
        {
            throw new InvalidDataException(
                "Archive external payload size does not match its canonical byte count.");
        }

        ValidateExternalAddress(sha256, relativePath, sizeBytes);
    }

    private void ValidateExternalAddress(
        string sha256,
        string relativePath,
        long sizeBytes)
    {
        if (sha256.Length != 64 ||
            sha256.Any(character =>
                !((character >= '0' && character <= '9') ||
                  (character >= 'a' && character <= 'f'))) ||
            sizeBytes < 0 ||
            string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException(
                "Archive contains invalid external payload address metadata.");
        }

        string candidatePath;
        try
        {
            candidatePath = Path.GetFullPath(
                Path.Combine(_filesRootPath, relativePath));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException(
                "Archive external payload relative path is invalid.",
                exception);
        }

        if (!candidatePath.StartsWith(
                _filesRootPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Archive external payload address escapes the configured Files root.");
        }
    }

    private static void ValidateForeignKeys(
        SqliteConnection connection,
        string databaseName)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        using SqliteDataReader reader = command.ExecuteReader();
        if (reader.Read())
        {
            throw new InvalidDataException(
                $"Database '{databaseName}' contains invalid foreign-key references during policy maintenance.");
        }
    }

    private static void EnableForeignKeys(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
    }

    private void EnsureActiveSession()
    {
        if (!_session.IsActive)
        {
            throw new OperationCanceledException(_session.CancellationToken);
        }
    }

    private static string ReadCanonicalGuid(
        SqliteDataReader reader,
        int ordinal,
        string errorMessage)
    {
        if (reader.GetValue(ordinal) is not string value ||
            !Guid.TryParseExact(value, "D", out Guid parsed) ||
            !string.Equals(
                value,
                parsed.ToString("D"),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(errorMessage);
        }

        return value;
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

    private sealed class ApplicationPolicyBuilder(
        ClipboardCapturePolicyRule captureRule)
    {
        public ClipboardCapturePolicyRule CaptureRule { get; } = captureRule;

        public Dictionary<string, ClipboardFormatCapturePolicy> Formats { get; } =
            new(StringComparer.Ordinal);
    }

    private sealed record CurrentSnapshot(
        PendingPolicyMaintenanceOperation Operation,
        GlobalPolicyMaintenanceState State,
        ClipboardCapturePolicy GlobalPolicy,
        IReadOnlyDictionary<string, ClipboardCapturePolicy> ApplicationPolicies);

    private sealed record ArchivePayloadKey(
        string EventId,
        long PayloadOrder,
        string FormatName,
        string PayloadKind);

    private sealed record ArchivePlan(
        ArchiveFileName FileName,
        Guid DatabaseId,
        DatabaseIdentity Identity,
        IReadOnlyList<ArchivePayloadKey> Deletions);
}
