using System.Globalization;
using Clipensk.Core.Clipboard;
using Clipensk.Core.Storage;
using Clipensk.Storage.Applications;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using DurableApplicationId = Clipensk.Core.Applications.ApplicationId;

namespace Clipensk.Storage.Clipboard;

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

    public ValueTask<ClipboardCapturePolicy?> GetApplicationPolicyAsync(
        DurableApplicationId applicationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(applicationId);
        using CancellationTokenSource linkedCancellation = CreateLinkedCancellation(cancellationToken);
        CancellationToken token = linkedCancellation.Token;
        token.ThrowIfCancellationRequested();

        using SqliteConnection connection = OpenValidatedCurrent(SqliteOpenMode.ReadOnly, token);
        ClipboardCapturePolicy? policy = ReadApplicationPolicy(connection, applicationId, token);
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(policy);
    }

    public ValueTask SetApplicationPolicyAsync(
        DurableApplicationId applicationId,
        ClipboardCapturePolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(applicationId);
        ArgumentNullException.ThrowIfNull(policy);
        ValidatePolicy(policy);

        using CancellationTokenSource linkedCancellation = CreateLinkedCancellation(cancellationToken);
        CancellationToken token = linkedCancellation.Token;
        token.ThrowIfCancellationRequested();

        using SqliteConnection connection = OpenValidatedCurrent(SqliteOpenMode.ReadWrite, token);
        using SqliteTransaction transaction = connection.BeginTransaction();

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
            upsert.Parameters.AddWithValue("$captureRule", FormatRule(policy.Capture));
            upsert.ExecuteNonQuery();
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

        foreach ((string formatName, ClipboardFormatCapturePolicy formatPolicy) in policy.Formats)
        {
            token.ThrowIfCancellationRequested();
            using SqliteCommand insertFormat = connection.CreateCommand();
            insertFormat.Transaction = transaction;
            insertFormat.CommandText = """
                INSERT INTO ApplicationFormatCapturePolicy (
                    ApplicationId, FormatName, CaptureRule, MaxBytes)
                VALUES ($applicationId, $formatName, $captureRule, $maxBytes);
                """;
            insertFormat.Parameters.AddWithValue("$applicationId", applicationId.ToString());
            insertFormat.Parameters.AddWithValue("$formatName", formatName);
            insertFormat.Parameters.AddWithValue("$captureRule", FormatRule(formatPolicy.Capture));
            insertFormat.Parameters.AddWithValue(
                "$maxBytes",
                formatPolicy.MaxBytes.HasValue
                    ? formatPolicy.MaxBytes.Value
                    : DBNull.Value);
            insertFormat.ExecuteNonQuery();
        }

        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteApplicationPolicyAsync(
        DurableApplicationId applicationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(applicationId);
        using CancellationTokenSource linkedCancellation = CreateLinkedCancellation(cancellationToken);
        CancellationToken token = linkedCancellation.Token;
        token.ThrowIfCancellationRequested();

        using SqliteConnection connection = OpenValidatedCurrent(SqliteOpenMode.ReadWrite, token);
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM ApplicationCapturePolicy
            WHERE ApplicationId = $applicationId;
            """;
        command.Parameters.AddWithValue("$applicationId", applicationId.ToString());
        command.ExecuteNonQuery();

        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return ValueTask.CompletedTask;
    }

    private CancellationTokenSource CreateLinkedCancellation(CancellationToken callerToken)
    {
        return CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            callerToken);
    }

    private SqliteConnection OpenValidatedCurrent(
        SqliteOpenMode mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
            cancellationToken.ThrowIfCancellationRequested();
            EnableForeignKeys(connection);
            ValidateCurrentDatabase(connection);
            ApplicationIdentitySqlSchema.ValidateTables(connection);
            ApplicationCapturePolicySqlSchema.ValidateTables(connection);
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
                schemaVersion < ApplicationCapturePolicySqlSchema.MinimumCurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    "Capture policy repository requires Current schema v3 or later.");
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

    private static ClipboardCapturePolicy? ReadApplicationPolicy(
        SqliteConnection connection,
        DurableApplicationId applicationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ClipboardCapturePolicyRule captureRule;
        using (SqliteCommand policyCommand = connection.CreateCommand())
        {
            policyCommand.CommandText = """
                SELECT CaptureRule
                FROM ApplicationCapturePolicy
                WHERE ApplicationId = $applicationId;
                """;
            policyCommand.Parameters.AddWithValue("$applicationId", applicationId.ToString());
            object? value = policyCommand.ExecuteScalar();
            if (value is null or DBNull)
            {
                return null;
            }

            if (value is not string text || !TryParseRule(text, out captureRule))
            {
                throw new InvalidDataException("Application capture policy contains an invalid capture rule.");
            }
        }

        var formats = new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal);
        using (SqliteCommand formatsCommand = connection.CreateCommand())
        {
            formatsCommand.CommandText = """
                SELECT FormatName, CaptureRule, MaxBytes
                FROM ApplicationFormatCapturePolicy
                WHERE ApplicationId = $applicationId
                ORDER BY FormatName COLLATE BINARY;
                """;
            formatsCommand.Parameters.AddWithValue("$applicationId", applicationId.ToString());
            using SqliteDataReader reader = formatsCommand.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string formatName = reader.GetString(0);
                if (string.IsNullOrWhiteSpace(formatName) ||
                    !TryParseRule(reader.GetString(1), out ClipboardCapturePolicyRule formatRule))
                {
                    throw new InvalidDataException(
                        "Application format capture policy contains invalid metadata.");
                }

                long? maxBytes = reader.IsDBNull(2) ? null : reader.GetInt64(2);
                if (maxBytes is <= 0)
                {
                    throw new InvalidDataException(
                        "Application format capture policy contains an invalid size limit.");
                }

                if (!formats.TryAdd(
                        formatName,
                        new ClipboardFormatCapturePolicy(formatRule, maxBytes)))
                {
                    throw new InvalidDataException(
                        "Application format capture policy contains duplicate format rows.");
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new ClipboardCapturePolicy(captureRule, formats);
    }

    private static void ValidatePolicy(ClipboardCapturePolicy policy)
    {
        ValidateRule(policy.Capture, nameof(policy));
        foreach ((string formatName, ClipboardFormatCapturePolicy formatPolicy) in policy.Formats)
        {
            if (string.IsNullOrWhiteSpace(formatName))
            {
                throw new ArgumentException("Clipboard format name cannot be empty.", nameof(policy));
            }
            ValidateRule(formatPolicy.Capture, nameof(policy));
            if (formatPolicy.MaxBytes is <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(policy),
                    "Configured clipboard format size limit must be positive.");
            }
        }
    }

    private static void ValidateRule(ClipboardCapturePolicyRule rule, string parameterName)
    {
        if (!Enum.IsDefined(rule))
        {
            throw new ArgumentOutOfRangeException(parameterName, rule, "Unknown capture policy rule.");
        }
    }

    private static string FormatRule(ClipboardCapturePolicyRule rule)
    {
        ValidateRule(rule, nameof(rule));
        return rule.ToString();
    }

    private static bool TryParseRule(string value, out ClipboardCapturePolicyRule rule)
    {
        return Enum.TryParse(value, ignoreCase: false, out rule) && Enum.IsDefined(rule);
    }

    private static void EnableForeignKeys(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
    }
}
