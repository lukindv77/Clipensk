using System.Globalization;
using Clipensk.Core.Clipboard;
using Clipensk.Core.Storage;
using Clipensk.Storage.ExternalFiles;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Clipboard;

public sealed record InitialCustomBinaryFormatConfiguration(
    string FormatName,
    string FileExtension);

/// <summary>
/// Atomically initializes the first global capture policy and the custom-binary
/// file-extension mappings that belong to that initial setup.
/// </summary>
public sealed class SqliteInitialClipboardCaptureConfigurationService
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly string _currentDatabasePath;

    public SqliteInitialClipboardCaptureConfigurationService(
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

    public ValueTask InitializeAsync(
        ClipboardCapturePolicy policy,
        IReadOnlyList<InitialCustomBinaryFormatConfiguration> customBinaryFormats,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(customBinaryFormats);
        ValidateExplicitRule(policy.Capture);
        foreach (ClipboardFormatCapturePolicy format in policy.Formats.Values)
        {
            ValidateExplicitRule(format.Capture);
        }

        var normalizedCustomFormats = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (InitialCustomBinaryFormatConfiguration configuration in customBinaryFormats)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentException.ThrowIfNullOrWhiteSpace(configuration.FormatName);
            if (!ClipboardCaptureFormatGuard.IsCaptureAllowed(configuration.FormatName))
            {
                throw new ArgumentException(
                    $"Clipboard format '{configuration.FormatName}' is prohibited from capture.",
                    nameof(customBinaryFormats));
            }

            if (!policy.Formats.TryGetValue(configuration.FormatName, out ClipboardFormatCapturePolicy? formatPolicy) ||
                formatPolicy.Capture != ClipboardCapturePolicyRule.Allow)
            {
                throw new ArgumentException(
                    "Every custom-binary extension mapping must belong to an explicitly allowed format in the initial policy.",
                    nameof(customBinaryFormats));
            }

            string normalizedExtension = ExternalPayloadAddressFactory
                .NormalizeCustomBinaryExtension(configuration.FileExtension);
            if (!normalizedCustomFormats.TryAdd(configuration.FormatName, normalizedExtension))
            {
                throw new ArgumentException(
                    "Custom-binary format names must be unique.",
                    nameof(customBinaryFormats));
            }
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            cancellationToken);
        CancellationToken token = linked.Token;
        using SqliteConnection connection = OpenValidatedCurrent(SqliteOpenMode.ReadWrite, token);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        if (HasAnyInitialConfigurationRows(connection, transaction, token))
        {
            throw new InvalidOperationException(
                "Initial clipboard capture configuration already contains durable rows; changes require cleanup.");
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
            command.Parameters.AddWithValue(
                "$maxBytes",
                format.MaxBytes.HasValue ? format.MaxBytes.Value : DBNull.Value);
            command.ExecuteNonQuery();
        }

        foreach ((string formatName, string fileExtension) in normalizedCustomFormats)
        {
            token.ThrowIfCancellationRequested();
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO CustomBinaryFormatConfiguration (FormatName, FileExtension)
                VALUES ($formatName, $fileExtension);
                """;
            command.Parameters.AddWithValue("$formatName", formatName);
            command.Parameters.AddWithValue("$fileExtension", fileExtension);
            command.ExecuteNonQuery();
        }

        token.ThrowIfCancellationRequested();
        transaction.Commit();
        // A committed aggregate setup remains successful even if cancellation arrives afterwards.
        return ValueTask.CompletedTask;
    }

    private SqliteConnection OpenValidatedCurrent(SqliteOpenMode mode, CancellationToken token)
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
            token.ThrowIfCancellationRequested();
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA foreign_keys = ON;";
                command.ExecuteNonQuery();
            }

            ValidateIdentity(connection);
            GlobalCapturePolicySqlSchema.ValidateTables(connection);
            CustomBinaryFormatConfigurationSqlSchema.ValidateTable(connection);
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
                throw new InvalidDataException(
                    "Initial clipboard capture configuration requires the expected Current identity.");
            }

            version = reader.GetInt32(3);
            if (version < CustomBinaryFormatConfigurationSqlSchema.MinimumCurrentSchemaVersion || reader.Read())
            {
                throw new InvalidDataException(
                    "Initial clipboard capture configuration requires Current schema v6 or later.");
            }
        }

        using SqliteCommand userVersion = connection.CreateCommand();
        userVersion.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) != version)
        {
            throw new InvalidDataException(
                "Current user_version does not match its initial capture configuration identity.");
        }
    }

    private static bool HasAnyInitialConfigurationRows(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM GlobalCapturePolicy) +
                (SELECT COUNT(*) FROM GlobalFormatCapturePolicy) +
                (SELECT COUNT(*) FROM CustomBinaryFormatConfiguration);
            """;
        long count = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        token.ThrowIfCancellationRequested();
        return count != 0;
    }

    private static void ValidateExplicitRule(ClipboardCapturePolicyRule rule)
    {
        if (rule is not (ClipboardCapturePolicyRule.Allow or ClipboardCapturePolicyRule.Deny))
        {
            throw new ArgumentOutOfRangeException(
                nameof(rule),
                rule,
                "Global capture rules must be explicit Allow or Deny.");
        }
    }
}
