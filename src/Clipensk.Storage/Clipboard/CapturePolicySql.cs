using Clipensk.Core.Clipboard;
using Clipensk.Storage.ExternalFiles;
using Microsoft.Data.Sqlite;
using DurableApplicationId = Clipensk.Core.Applications.ApplicationId;

namespace Clipensk.Storage.Clipboard;

/// <summary>
/// Reads, validates and writes capture policies inside a caller-owned Current transaction, for the
/// maintenance services that hold the storage mutation lease.
/// </summary>
internal static class CapturePolicySql
{
    public static void ValidateGlobalPolicy(ClipboardCapturePolicy policy)
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

    public static void ValidateApplicationPolicy(ClipboardCapturePolicy policy)
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

    public static Dictionary<string, string> NormalizeCustomBinaryConfigurations(
        ClipboardCapturePolicy policy,
        IReadOnlyList<ApplicationCustomBinaryFormatConfiguration> configurations)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(configurations);
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

    public static ClipboardCapturePolicy? ReadGlobalPolicyInTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ClipboardCapturePolicyRule capture;
        using (SqliteCommand header = connection.CreateCommand())
        {
            header.Transaction = transaction;
            header.CommandText = "SELECT CaptureRule FROM GlobalCapturePolicy WHERE SingletonId = 1;";
            object? value = header.ExecuteScalar();
            if (value is null or DBNull)
            {
                return null;
            }
            capture = ParseExplicitRule(value as string);
        }

        var formats = new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT FormatName, CaptureRule, MaxBytes
            FROM GlobalFormatCapturePolicy
            WHERE SingletonId = 1
            ORDER BY FormatName COLLATE BINARY;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            string formatName = reader.GetString(0);
            if (string.IsNullOrWhiteSpace(formatName) ||
                !formats.TryAdd(
                    formatName,
                    new ClipboardFormatCapturePolicy(
                        ParseExplicitRule(reader.GetString(1)),
                        ReadMaxBytes(reader, 2))))
            {
                throw new InvalidDataException("Global capture policy contains invalid or duplicate formats.");
            }
        }

        return new ClipboardCapturePolicy(capture, formats);
    }

    public static ClipboardCapturePolicy? ReadApplicationPolicyInTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DurableApplicationId applicationId,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ClipboardCapturePolicyRule capture;
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
            capture = ParseApplicationRule(value as string);
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
            if (string.IsNullOrWhiteSpace(formatName) ||
                !formats.TryAdd(
                    formatName,
                    new ClipboardFormatCapturePolicy(
                        ParseApplicationRule(reader.GetString(1)),
                        ReadMaxBytes(reader, 2))))
            {
                throw new InvalidDataException("Application capture policy contains invalid or duplicate formats.");
            }
        }

        return new ClipboardCapturePolicy(capture, formats);
    }

    public static void ReplaceGlobalPolicyInTransaction(
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

    /// <summary>
    /// Writes a personal policy. <paramref name="replaceExisting"/> distinguishes an edit, which
    /// requires the policy row to exist, from a first assignment, which requires it to be absent.
    /// </summary>
    public static void WriteApplicationPolicyInTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DurableApplicationId applicationId,
        ClipboardCapturePolicy policy,
        bool replaceExisting,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using (SqliteCommand header = connection.CreateCommand())
        {
            header.Transaction = transaction;
            header.CommandText = replaceExisting
                ? """
                  UPDATE ApplicationCapturePolicy
                  SET CaptureRule = $captureRule
                  WHERE ApplicationId = $applicationId;
                  """
                : """
                  INSERT INTO ApplicationCapturePolicy (ApplicationId, CaptureRule)
                  VALUES ($applicationId, $captureRule);
                  """;
            header.Parameters.AddWithValue("$applicationId", applicationId.ToString());
            header.Parameters.AddWithValue("$captureRule", policy.Capture.ToString());
            if (header.ExecuteNonQuery() != 1)
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

    public static void DeleteApplicationPolicyInTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DurableApplicationId applicationId)
    {
        using SqliteCommand delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = """
            DELETE FROM ApplicationFormatCapturePolicy WHERE ApplicationId = $applicationId;
            DELETE FROM ApplicationCapturePolicy WHERE ApplicationId = $applicationId;
            """;
        delete.Parameters.AddWithValue("$applicationId", applicationId.ToString());
        delete.ExecuteNonQuery();
    }

    public static void InsertMissingCustomBinaryConfigurationsInTransaction(
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

    private static ClipboardCapturePolicyRule ParseExplicitRule(string? value) => value switch
    {
        "Allow" => ClipboardCapturePolicyRule.Allow,
        "Deny" => ClipboardCapturePolicyRule.Deny,
        _ => throw new InvalidDataException("Persisted global capture rules must be explicit Allow or Deny."),
    };

    private static ClipboardCapturePolicyRule ParseApplicationRule(string? value) => value switch
    {
        "Inherit" => ClipboardCapturePolicyRule.Inherit,
        "Allow" => ClipboardCapturePolicyRule.Allow,
        "Deny" => ClipboardCapturePolicyRule.Deny,
        _ => throw new InvalidDataException("Persisted application capture policy contains an unknown rule."),
    };

    private static long? ReadMaxBytes(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }
        if (reader.GetValue(ordinal) is not long maxBytes || maxBytes <= 0)
        {
            throw new InvalidDataException("Capture policy contains an invalid MaxBytes value.");
        }
        return maxBytes;
    }
}
