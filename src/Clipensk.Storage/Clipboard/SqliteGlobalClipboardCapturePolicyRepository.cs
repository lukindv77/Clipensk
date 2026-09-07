using System.Globalization;
using Clipensk.Core.Clipboard;
using Clipensk.Core.Storage;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Clipboard;

public sealed class SqliteGlobalClipboardCapturePolicyRepository : IGlobalClipboardCapturePolicyRepository
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly string _currentDatabasePath;

    public SqliteGlobalClipboardCapturePolicyRepository(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
        _currentDatabasePath = Path.Combine(Path.GetFullPath(session.DataRootPath), "Current", "current.db");
    }

    public ValueTask<ClipboardCapturePolicy?> ReadAsync(CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked = CreateLinkedCancellation(cancellationToken);
        CancellationToken token = linked.Token;
        using SqliteConnection connection = OpenValidatedCurrent(SqliteOpenMode.ReadOnly, token);
        ClipboardCapturePolicy? policy = ReadPolicy(connection, transaction: null, token);
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(policy);
    }

    public ValueTask InitializeAsync(
        ClipboardCapturePolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ValidateExplicitRule(policy.Capture);
        foreach (ClipboardFormatCapturePolicy format in policy.Formats.Values)
        {
            ValidateExplicitRule(format.Capture);
        }

        using CancellationTokenSource linked = CreateLinkedCancellation(cancellationToken);
        CancellationToken token = linked.Token;
        using SqliteConnection connection = OpenValidatedCurrent(SqliteOpenMode.ReadWrite, token);
        // Immediate write transaction serializes competing first-time setup attempts.
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        if (ReadPolicy(connection, transaction, token) is not null)
        {
            throw new InvalidOperationException(
                "Global capture policy is already configured; changes require policy cleanup.");
        }

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO GlobalCapturePolicy (SingletonId, CaptureRule)
                VALUES (1, $captureRule);
                """;
            command.Parameters.AddWithValue("$captureRule", policy.Capture.ToString());
            command.ExecuteNonQuery();
        }

        foreach ((string formatName, ClipboardFormatCapturePolicy format) in policy.Formats)
        {
            token.ThrowIfCancellationRequested();
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO GlobalFormatCapturePolicy (SingletonId, FormatName, CaptureRule, MaxBytes)
                VALUES (1, $formatName, $captureRule, $maxBytes);
                """;
            command.Parameters.AddWithValue("$formatName", formatName);
            command.Parameters.AddWithValue("$captureRule", format.Capture.ToString());
            command.Parameters.AddWithValue("$maxBytes", format.MaxBytes.HasValue ? format.MaxBytes.Value : DBNull.Value);
            command.ExecuteNonQuery();
        }

        token.ThrowIfCancellationRequested();
        transaction.Commit();
        // A committed policy remains a successful write even if cancellation arrives afterwards.
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> CleanupAsync(CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked = CreateLinkedCancellation(cancellationToken);
        CancellationToken token = linked.Token;
        using SqliteConnection connection = OpenValidatedCurrent(SqliteOpenMode.ReadWrite, token);
        // Immediate transaction linearizes cleanup against first-time initialization and validates
        // the complete persisted policy before deleting any row.
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        ClipboardCapturePolicy? existing = ReadPolicy(connection, transaction, token);
        if (existing is null)
        {
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return ValueTask.FromResult(false);
        }

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM GlobalCapturePolicy WHERE SingletonId = 1;";
            if (command.ExecuteNonQuery() != 1)
            {
                throw new InvalidDataException("Global capture policy cleanup did not remove the expected singleton row.");
            }
        }

        token.ThrowIfCancellationRequested();
        transaction.Commit();
        // GlobalFormatCapturePolicy rows are removed by the validated ON DELETE CASCADE foreign key.
        // Do not demote a committed cleanup when cancellation arrives after COMMIT.
        return ValueTask.FromResult(true);
    }

    private CancellationTokenSource CreateLinkedCancellation(CancellationToken callerToken) =>
        CancellationTokenSource.CreateLinkedTokenSource(_session.CancellationToken, callerToken);

    private SqliteConnection OpenValidatedCurrent(SqliteOpenMode mode, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_session.IsActive)
        {
            throw new OperationCanceledException(_session.CancellationToken);
        }

        SqliteConnection connection = _connectionFactory.Open(
            _currentDatabasePath, _session.DangerousGetMasterKeyMemory(), mode);
        try
        {
            token.ThrowIfCancellationRequested();
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA foreign_keys = ON;";
                command.ExecuteNonQuery();
            }
            ValidateIdentity(connection);
            GlobalCapturePolicySqlSchema.ValidateTables(connection);
            token.ThrowIfCancellationRequested();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void ValidateIdentity(SqliteConnection connection)
    {
        int version;
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT SingletonId, StorageId, DatabaseRole, SchemaVersion FROM DatabaseIdentity;";
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read() || reader.GetInt64(0) != 1 ||
                !Guid.TryParse(reader.GetString(1), out Guid storageId) || storageId != _session.StorageId ||
                !string.Equals(reader.GetString(2), DatabaseRole.Current.ToString(), StringComparison.Ordinal))
            {
                throw new InvalidDataException("Global capture policy requires the expected Current identity.");
            }
            version = reader.GetInt32(3);
            if (version < GlobalCapturePolicySqlSchema.MinimumCurrentSchemaVersion || reader.Read())
            {
                throw new InvalidDataException("Global capture policy requires Current schema v5 or later.");
            }
        }
        using SqliteCommand userVersion = connection.CreateCommand();
        userVersion.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) != version)
        {
            throw new InvalidDataException("Current user_version does not match its identity.");
        }
    }

    private static ClipboardCapturePolicy? ReadPolicy(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        // One statement gives a single snapshot and also exposes malformed orphan format rows.
        command.CommandText = """
            SELECT p.SingletonId, p.CaptureRule, f.FormatName, f.CaptureRule, f.MaxBytes
            FROM GlobalCapturePolicy p
            LEFT JOIN GlobalFormatCapturePolicy f ON f.SingletonId = p.SingletonId
            UNION ALL
            SELECT NULL, NULL, f.FormatName, f.CaptureRule, f.MaxBytes
            FROM GlobalFormatCapturePolicy f
            WHERE NOT EXISTS (SELECT 1 FROM GlobalCapturePolicy p WHERE p.SingletonId = f.SingletonId);
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        ClipboardCapturePolicyRule? capture = null;
        var formats = new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal);
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.IsDBNull(0) || reader.GetValue(0) is not long singletonId || singletonId != 1 ||
                reader.GetValue(1) is not string ruleText)
            {
                throw new InvalidDataException("Global capture policy contains invalid or orphan rows.");
            }
            ClipboardCapturePolicyRule rule = ParseStoredRule(ruleText);
            if (capture.HasValue && capture.Value != rule)
            {
                throw new InvalidDataException("Global capture policy contains conflicting rules.");
            }
            capture = rule;
            if (reader.IsDBNull(2))
            {
                continue;
            }
            if (reader.GetValue(2) is not string name || string.IsNullOrWhiteSpace(name) ||
                reader.GetValue(3) is not string formatRuleText)
            {
                throw new InvalidDataException("Global format policy contains invalid metadata.");
            }
            long? maxBytes = null;
            if (!reader.IsDBNull(4))
            {
                if (reader.GetValue(4) is not long size || size <= 0)
                {
                    throw new InvalidDataException("Global format policy size limit must be a positive integer.");
                }
                maxBytes = size;
            }
            if (!formats.TryAdd(name, new ClipboardFormatCapturePolicy(ParseStoredRule(formatRuleText), maxBytes)))
            {
                throw new InvalidDataException("Global capture policy contains duplicate format names.");
            }
        }
        token.ThrowIfCancellationRequested();
        return capture.HasValue ? new ClipboardCapturePolicy(capture.Value, formats) : null;
    }

    private static ClipboardCapturePolicyRule ParseStoredRule(string value) => value switch
    {
        "Allow" => ClipboardCapturePolicyRule.Allow,
        "Deny" => ClipboardCapturePolicyRule.Deny,
        _ => throw new InvalidDataException("Global capture rules must be explicit Allow or Deny."),
    };

    private static void ValidateExplicitRule(ClipboardCapturePolicyRule rule)
    {
        if (rule is not (ClipboardCapturePolicyRule.Allow or ClipboardCapturePolicyRule.Deny))
        {
            throw new ArgumentOutOfRangeException(nameof(rule), rule, "Global capture rules must be explicit Allow or Deny.");
        }
    }
}