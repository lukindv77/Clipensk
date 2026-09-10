using System.Globalization;
using Clipensk.Core.Applications;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Applications;

public sealed class SqliteApplicationDiscoveredFormatRepository :
    IApplicationDiscoveredFormatRepository
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly string _currentDatabasePath;

    public SqliteApplicationDiscoveredFormatRepository(
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

    public ValueTask ObserveAsync(
        ApplicationId applicationId,
        IReadOnlyCollection<string> formatNames,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(applicationId);
        ArgumentNullException.ThrowIfNull(formatNames);

        string[] names = NormalizeFormatNames(formatNames);
        if (names.Length == 0)
        {
            return ValueTask.CompletedTask;
        }

        using CancellationTokenSource linkedCancellation = CreateLinkedCancellation(cancellationToken);
        CancellationToken token = linkedCancellation.Token;
        token.ThrowIfCancellationRequested();

        string observedAt = observedAtUtc
            .ToUniversalTime()
            .ToString("O", CultureInfo.InvariantCulture);

        using SqliteConnection connection = OpenValidatedCurrent(SqliteOpenMode.ReadWrite, token);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        foreach (string formatName in names)
        {
            token.ThrowIfCancellationRequested();
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO ApplicationDiscoveredFormat (
                    ApplicationId, FormatName, FirstSeenAtUtc, LastSeenAtUtc)
                VALUES ($applicationId, $formatName, $observedAtUtc, $observedAtUtc)
                ON CONFLICT(ApplicationId, FormatName) DO UPDATE SET
                    FirstSeenAtUtc = CASE
                        WHEN excluded.FirstSeenAtUtc < ApplicationDiscoveredFormat.FirstSeenAtUtc
                            THEN excluded.FirstSeenAtUtc
                        ELSE ApplicationDiscoveredFormat.FirstSeenAtUtc
                    END,
                    LastSeenAtUtc = CASE
                        WHEN excluded.LastSeenAtUtc > ApplicationDiscoveredFormat.LastSeenAtUtc
                            THEN excluded.LastSeenAtUtc
                        ELSE ApplicationDiscoveredFormat.LastSeenAtUtc
                    END;
                """;
            command.Parameters.AddWithValue("$applicationId", applicationId.ToString());
            command.Parameters.AddWithValue("$formatName", formatName);
            command.Parameters.AddWithValue("$observedAtUtc", observedAt);
            command.ExecuteNonQuery();
        }

        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<ApplicationDiscoveredFormat>> ListAsync(
        ApplicationId applicationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(applicationId);
        using CancellationTokenSource linkedCancellation = CreateLinkedCancellation(cancellationToken);
        CancellationToken token = linkedCancellation.Token;
        token.ThrowIfCancellationRequested();

        using SqliteConnection connection = OpenValidatedCurrent(SqliteOpenMode.ReadOnly, token);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT FormatName, FirstSeenAtUtc, LastSeenAtUtc
            FROM ApplicationDiscoveredFormat
            WHERE ApplicationId = $applicationId
            ORDER BY FormatName COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$applicationId", applicationId.ToString());

        var result = new List<ApplicationDiscoveredFormat>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            string formatName = reader.GetString(0);
            if (string.IsNullOrWhiteSpace(formatName))
            {
                throw new InvalidDataException(
                    "Application discovered format contains an empty format name.");
            }

            DateTimeOffset firstSeenAtUtc = ParsePersistedUtcTimestamp(reader.GetString(1));
            DateTimeOffset lastSeenAtUtc = ParsePersistedUtcTimestamp(reader.GetString(2));
            if (lastSeenAtUtc < firstSeenAtUtc)
            {
                throw new InvalidDataException(
                    "Application discovered format has a LastSeenAtUtc earlier than FirstSeenAtUtc.");
            }

            result.Add(new ApplicationDiscoveredFormat(
                applicationId,
                formatName,
                firstSeenAtUtc,
                lastSeenAtUtc));
        }

        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<ApplicationDiscoveredFormat>>(result.ToArray());
    }

    private CancellationTokenSource CreateLinkedCancellation(CancellationToken callerToken) =>
        CancellationTokenSource.CreateLinkedTokenSource(_session.CancellationToken, callerToken);

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
            ApplicationDiscoveredFormatSqlSchema.ValidateTable(connection);
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
                    "Application discovered formats require the expected Current database identity.");
            }

            schemaVersion = reader.GetInt32(2);
            if (!Guid.TryParse(reader.GetString(0), out Guid storageId) ||
                storageId != _session.StorageId ||
                !string.Equals(reader.GetString(1), DatabaseRole.Current.ToString(), StringComparison.Ordinal) ||
                schemaVersion < ApplicationDiscoveredFormatSqlSchema.MinimumCurrentSchemaVersion ||
                reader.Read())
            {
                throw new InvalidDataException(
                    "Application discovered formats require the expected Current database identity and schema version.");
            }
        }

        using SqliteCommand userVersion = connection.CreateCommand();
        userVersion.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) != schemaVersion)
        {
            throw new InvalidDataException(
                "Current user_version does not match its application discovered format schema contract.");
        }
    }

    private static string[] NormalizeFormatNames(IReadOnlyCollection<string> formatNames)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string formatName in formatNames)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(formatName);
            names.Add(formatName);
        }

        return names.OrderBy(name => name, StringComparer.Ordinal).ToArray();
    }

    private static DateTimeOffset ParsePersistedUtcTimestamp(string value)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTimeOffset parsed) ||
            parsed.Offset != TimeSpan.Zero ||
            !string.Equals(
                value,
                parsed.ToString("O", CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Application discovered format contains a non-canonical UTC timestamp.");
        }

        return parsed;
    }

    private static void EnableForeignKeys(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
    }
}
