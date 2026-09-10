using System.Globalization;
using Clipensk.Core.Applications;
using Clipensk.Core.Storage;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Applications;

public sealed class SqliteApplicationIdentityCatalog : IApplicationIdentityCatalog
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly string _currentDatabasePath;

    public SqliteApplicationIdentityCatalog(
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

    public ValueTask<IReadOnlyList<ApplicationIdentityCatalogEntry>> ReadAllAsync(
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            cancellationToken);
        CancellationToken token = linkedCancellation.Token;
        token.ThrowIfCancellationRequested();

        using SqliteConnection connection = OpenValidatedCurrent(token);
        Dictionary<ApplicationId, EntryBuilder> entries = ReadIdentities(connection, token);
        ReadAliases(connection, entries, token);

        ApplicationIdentityCatalogEntry[] result = entries.Values
            .OrderByDescending(entry => entry.CreatedAtUtc)
            .ThenBy(entry => entry.ApplicationId.Value)
            .Select(entry => new ApplicationIdentityCatalogEntry(
                entry.ApplicationId,
                entry.CreatedAtUtc,
                entry.ApplicationUserModelIds.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                entry.ExecutablePaths.OrderBy(value => value, StringComparer.Ordinal).ToArray()))
            .ToArray();

        token.ThrowIfCancellationRequested();
        return new ValueTask<IReadOnlyList<ApplicationIdentityCatalogEntry>>(result);
    }

    private SqliteConnection OpenValidatedCurrent(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_session.IsActive)
        {
            throw new OperationCanceledException(_session.CancellationToken);
        }

        SqliteConnection connection = _connectionFactory.Open(
            _currentDatabasePath,
            _session.DangerousGetMasterKeyMemory(),
            SqliteOpenMode.ReadOnly);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnableForeignKeys(connection);
            ValidateCurrentDatabase(connection);
            ApplicationIdentitySqlSchema.ValidateTables(connection);
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
                    "Application identity catalog requires the expected Current database identity.");
            }

            schemaVersion = reader.GetInt32(2);
            if (!Guid.TryParse(reader.GetString(0), out Guid storageId) ||
                storageId != _session.StorageId ||
                !string.Equals(reader.GetString(1), DatabaseRole.Current.ToString(), StringComparison.Ordinal) ||
                schemaVersion < ApplicationIdentitySqlSchema.MinimumCurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    "Application identity catalog requires the expected Current database identity and schema version.");
            }
        }

        using SqliteCommand userVersion = connection.CreateCommand();
        userVersion.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) != schemaVersion)
        {
            throw new InvalidDataException(
                "Current database user_version does not match the application identity schema contract.");
        }
    }

    private static Dictionary<ApplicationId, EntryBuilder> ReadIdentities(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var entries = new Dictionary<ApplicationId, EntryBuilder>();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT ApplicationId, CreatedAtUtc
            FROM ApplicationIdentity;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplicationId applicationId = ParseApplicationId(reader.GetString(0));
            DateTimeOffset createdAtUtc = ParseUtcTimestamp(reader.GetString(1), "application identity");
            if (!entries.TryAdd(applicationId, new EntryBuilder(applicationId, createdAtUtc)))
            {
                throw new InvalidDataException(
                    "Application identity catalog contains a duplicate durable ApplicationId.");
            }
        }

        return entries;
    }

    private static void ReadAliases(
        SqliteConnection connection,
        IReadOnlyDictionary<ApplicationId, EntryBuilder> entries,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT AliasType, AliasValue, ApplicationId, CreatedAtUtc
            FROM ApplicationIdentityAlias;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string aliasType = reader.GetString(0);
            string aliasValue = reader.GetString(1);
            if (string.IsNullOrWhiteSpace(aliasValue))
            {
                throw new InvalidDataException(
                    "Application identity catalog contains an empty durable alias value.");
            }

            ApplicationId applicationId = ParseApplicationId(reader.GetString(2));
            _ = ParseUtcTimestamp(reader.GetString(3), "application identity alias");
            if (!entries.TryGetValue(applicationId, out EntryBuilder? entry))
            {
                throw new InvalidDataException(
                    "Application identity catalog contains an alias for an unknown durable ApplicationId.");
            }

            HashSet<string> target = aliasType switch
            {
                ApplicationIdentitySqlSchema.AumidAliasType => entry.ApplicationUserModelIds,
                ApplicationIdentitySqlSchema.ExecutablePathAliasType => entry.ExecutablePaths,
                _ => throw new InvalidDataException(
                    "Application identity catalog contains an unsupported durable alias type."),
            };

            if (!target.Add(aliasValue))
            {
                throw new InvalidDataException(
                    "Application identity catalog contains a duplicate durable alias value.");
            }
        }
    }

    private static ApplicationId ParseApplicationId(string value)
    {
        if (!Guid.TryParse(value, out Guid parsed) || parsed == Guid.Empty)
        {
            throw new InvalidDataException(
                "Application identity catalog contains an invalid durable ApplicationId.");
        }

        return new ApplicationId(parsed);
    }

    private static DateTimeOffset ParseUtcTimestamp(string value, string description)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTimeOffset parsed) ||
            parsed.Offset != TimeSpan.Zero ||
            !string.Equals(
                parsed.ToString("O", CultureInfo.InvariantCulture),
                value,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Application identity catalog contains an invalid {description} UTC timestamp.");
        }

        return parsed;
    }

    private static void EnableForeignKeys(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
    }

    private sealed class EntryBuilder
    {
        public EntryBuilder(ApplicationId applicationId, DateTimeOffset createdAtUtc)
        {
            ApplicationId = applicationId;
            CreatedAtUtc = createdAtUtc;
        }

        public ApplicationId ApplicationId { get; }

        public DateTimeOffset CreatedAtUtc { get; }

        public HashSet<string> ApplicationUserModelIds { get; } = new(StringComparer.Ordinal);

        public HashSet<string> ExecutablePaths { get; } = new(StringComparer.Ordinal);
    }
}
