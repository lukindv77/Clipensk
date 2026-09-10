using System.Globalization;
using Clipensk.Core.Applications;
using Clipensk.Core.Storage;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Clipensk.Storage.Applications;

public sealed class SqliteApplicationIdentityRepository :
    IApplicationIdentityRepository,
    IApplicationIdentityReadRepository
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly string _currentDatabasePath;

    public SqliteApplicationIdentityRepository(
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

    public ValueTask<ApplicationIdentityAliasLookup> FindAliasesAsync(
        ApplicationIdentityObservation observation,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linkedCancellation = CreateLinkedCancellation(cancellationToken);
        CancellationToken token = linkedCancellation.Token;
        token.ThrowIfCancellationRequested();

        using SqliteConnection connection = OpenValidatedCurrent(SqliteOpenMode.ReadOnly, token);
        ApplicationIdentityAliasLookup result = FindAliases(connection, observation, token);
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(result);
    }

    public ValueTask<IReadOnlyList<ApplicationIdentitySummary>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linkedCancellation = CreateLinkedCancellation(cancellationToken);
        CancellationToken token = linkedCancellation.Token;
        token.ThrowIfCancellationRequested();

        using SqliteConnection connection = OpenValidatedCurrent(SqliteOpenMode.ReadOnly, token);
        IReadOnlyList<ApplicationIdentitySummary> result = List(connection, token);
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(result);
    }

    public ValueTask<ApplicationId> CreateAndBindAsync(
        ApplicationIdentityObservation observation,
        ApplicationIdentityResolutionBasis basis,
        CancellationToken cancellationToken = default)
    {
        ValidateCreateRequest(observation, basis);

        using CancellationTokenSource linkedCancellation = CreateLinkedCancellation(cancellationToken);
        CancellationToken token = linkedCancellation.Token;
        token.ThrowIfCancellationRequested();

        using SqliteConnection connection = OpenValidatedCurrent(SqliteOpenMode.ReadWrite, token);
        ApplicationId applicationId = ApplicationId.New();
        string createdAtUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        try
        {
            using (SqliteTransaction transaction = connection.BeginTransaction())
            {
                InsertApplication(connection, transaction, applicationId, createdAtUtc);

                if (!string.IsNullOrWhiteSpace(observation.ApplicationUserModelId))
                {
                    InsertAlias(
                        connection,
                        transaction,
                        ApplicationIdentitySqlSchema.AumidAliasType,
                        observation.ApplicationUserModelId,
                        applicationId,
                        createdAtUtc);
                }

                if (!string.IsNullOrWhiteSpace(observation.ExecutablePath))
                {
                    InsertAlias(
                        connection,
                        transaction,
                        ApplicationIdentitySqlSchema.ExecutablePathAliasType,
                        observation.ExecutablePath,
                        applicationId,
                        createdAtUtc);
                }

                token.ThrowIfCancellationRequested();
                transaction.Commit();
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == raw.SQLITE_CONSTRAINT)
        {
            ApplicationIdentityAliasLookup lookup = FindAliases(connection, observation, token);
            if (lookup.ApplicationUserModelIdApplicationId is not null ||
                lookup.ExecutablePathApplicationId is not null)
            {
                throw CreateConflict(observation, lookup);
            }

            throw;
        }

        return ValueTask.FromResult(applicationId);
    }

    public ValueTask BindExecutablePathAliasAsync(
        ApplicationId applicationId,
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(applicationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        using CancellationTokenSource linkedCancellation = CreateLinkedCancellation(cancellationToken);
        CancellationToken token = linkedCancellation.Token;
        token.ThrowIfCancellationRequested();

        using SqliteConnection connection = OpenValidatedCurrent(SqliteOpenMode.ReadWrite, token);

        ApplicationId? existing = FindAlias(
            connection,
            ApplicationIdentitySqlSchema.ExecutablePathAliasType,
            executablePath,
            token);
        if (existing is not null)
        {
            if (existing == applicationId)
            {
                return ValueTask.CompletedTask;
            }

            throw new ApplicationIdentityConflictException(
                new ApplicationIdentityObservation(null, executablePath),
                applicationUserModelIdApplicationId: null,
                executablePathApplicationId: existing);
        }

        string createdAtUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        try
        {
            using (SqliteTransaction transaction = connection.BeginTransaction())
            {
                InsertAlias(
                    connection,
                    transaction,
                    ApplicationIdentitySqlSchema.ExecutablePathAliasType,
                    executablePath,
                    applicationId,
                    createdAtUtc);
                token.ThrowIfCancellationRequested();
                transaction.Commit();
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == raw.SQLITE_CONSTRAINT)
        {
            ApplicationId? raced = FindAlias(
                connection,
                ApplicationIdentitySqlSchema.ExecutablePathAliasType,
                executablePath,
                token);
            if (raced == applicationId)
            {
                return ValueTask.CompletedTask;
            }
            if (raced is not null)
            {
                throw new ApplicationIdentityConflictException(
                    new ApplicationIdentityObservation(null, executablePath),
                    applicationUserModelIdApplicationId: null,
                    executablePathApplicationId: raced);
            }

            throw;
        }

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
                    "Application identity repository requires the expected Current database identity.");
            }

            schemaVersion = reader.GetInt32(2);
            if (!Guid.TryParse(reader.GetString(0), out Guid storageId) ||
                storageId != _session.StorageId ||
                !string.Equals(reader.GetString(1), DatabaseRole.Current.ToString(), StringComparison.Ordinal) ||
                schemaVersion < ApplicationIdentitySqlSchema.MinimumCurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    "Application identity repository requires the expected Current database identity and schema version.");
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

    private static void EnableForeignKeys(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<ApplicationIdentitySummary> List(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.ApplicationId, i.CreatedAtUtc, a.AliasType, a.AliasValue
            FROM ApplicationIdentity AS i
            LEFT JOIN ApplicationIdentityAlias AS a
              ON a.ApplicationId = i.ApplicationId
            ORDER BY i.ApplicationId COLLATE BINARY,
                     a.AliasType COLLATE BINARY,
                     a.AliasValue COLLATE BINARY;
            """;

        var builders = new List<ApplicationIdentitySummaryBuilder>();
        string? currentApplicationIdText = null;
        ApplicationIdentitySummaryBuilder? current = null;

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string applicationIdText = reader.GetString(0);
            ApplicationId applicationId = ParsePersistedApplicationId(applicationIdText);
            DateTimeOffset createdAtUtc = ParsePersistedCreatedAtUtc(reader.GetString(1));

            if (!string.Equals(
                    currentApplicationIdText,
                    applicationIdText,
                    StringComparison.Ordinal))
            {
                currentApplicationIdText = applicationIdText;
                current = new ApplicationIdentitySummaryBuilder(applicationId, createdAtUtc);
                builders.Add(current);
            }
            else if (current is null || current.CreatedAtUtc != createdAtUtc)
            {
                throw new InvalidDataException(
                    "Application identity rows contain inconsistent creation timestamps.");
            }

            bool hasAliasType = !reader.IsDBNull(2);
            bool hasAliasValue = !reader.IsDBNull(3);
            if (hasAliasType != hasAliasValue)
            {
                throw new InvalidDataException(
                    "Application identity alias contains incomplete persisted data.");
            }
            if (!hasAliasType)
            {
                continue;
            }

            string aliasType = reader.GetString(2);
            string aliasValue = reader.GetString(3);
            if (string.IsNullOrWhiteSpace(aliasValue))
            {
                throw new InvalidDataException(
                    "Application identity alias contains an empty value.");
            }

            switch (aliasType)
            {
                case ApplicationIdentitySqlSchema.AumidAliasType:
                    current!.ApplicationUserModelIds.Add(aliasValue);
                    break;
                case ApplicationIdentitySqlSchema.ExecutablePathAliasType:
                    current!.ExecutablePaths.Add(aliasValue);
                    break;
                default:
                    throw new InvalidDataException(
                        "Application identity alias contains an unknown alias type.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return builders
            .Select(builder => new ApplicationIdentitySummary(
                builder.ApplicationId,
                builder.CreatedAtUtc,
                builder.ApplicationUserModelIds.ToArray(),
                builder.ExecutablePaths.ToArray()))
            .ToArray();
    }

    private static ApplicationIdentityAliasLookup FindAliases(
        SqliteConnection connection,
        ApplicationIdentityObservation observation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApplicationId? aumid = string.IsNullOrWhiteSpace(observation.ApplicationUserModelId)
            ? null
            : FindAlias(
                connection,
                ApplicationIdentitySqlSchema.AumidAliasType,
                observation.ApplicationUserModelId,
                cancellationToken);

        ApplicationId? path = string.IsNullOrWhiteSpace(observation.ExecutablePath)
            ? null
            : FindAlias(
                connection,
                ApplicationIdentitySqlSchema.ExecutablePathAliasType,
                observation.ExecutablePath,
                cancellationToken);

        return new ApplicationIdentityAliasLookup(aumid, path);
    }

    private static ApplicationId? FindAlias(
        SqliteConnection connection,
        string aliasType,
        string aliasValue,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT ApplicationId
            FROM ApplicationIdentityAlias
            WHERE AliasType = $aliasType AND AliasValue = $aliasValue;
            """;
        command.Parameters.AddWithValue("$aliasType", aliasType);
        command.Parameters.AddWithValue("$aliasValue", aliasValue);

        object? value = command.ExecuteScalar();
        cancellationToken.ThrowIfCancellationRequested();
        if (value is null or DBNull)
        {
            return null;
        }

        if (value is not string text ||
            !Guid.TryParse(text, out Guid applicationId) ||
            applicationId == Guid.Empty)
        {
            throw new InvalidDataException("Application identity alias contains an invalid durable ApplicationId.");
        }

        return new ApplicationId(applicationId);
    }

    private static ApplicationId ParsePersistedApplicationId(string value)
    {
        if (!Guid.TryParseExact(value, "D", out Guid parsed) ||
            parsed == Guid.Empty ||
            !string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Application identity contains a non-canonical durable ApplicationId.");
        }

        return new ApplicationId(parsed);
    }

    private static DateTimeOffset ParsePersistedCreatedAtUtc(string value)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTimeOffset parsed) ||
            parsed.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                "Application identity contains an invalid UTC creation timestamp.");
        }

        return parsed;
    }

    private static void InsertApplication(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ApplicationId applicationId,
        string createdAtUtc)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ApplicationIdentity (ApplicationId, CreatedAtUtc)
            VALUES ($applicationId, $createdAtUtc);
            """;
        command.Parameters.AddWithValue("$applicationId", applicationId.ToString());
        command.Parameters.AddWithValue("$createdAtUtc", createdAtUtc);
        command.ExecuteNonQuery();
    }

    private static void InsertAlias(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string aliasType,
        string aliasValue,
        ApplicationId applicationId,
        string createdAtUtc)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ApplicationIdentityAlias (
                AliasType, AliasValue, ApplicationId, CreatedAtUtc)
            VALUES ($aliasType, $aliasValue, $applicationId, $createdAtUtc);
            """;
        command.Parameters.AddWithValue("$aliasType", aliasType);
        command.Parameters.AddWithValue("$aliasValue", aliasValue);
        command.Parameters.AddWithValue("$applicationId", applicationId.ToString());
        command.Parameters.AddWithValue("$createdAtUtc", createdAtUtc);
        command.ExecuteNonQuery();
    }

    private static void ValidateCreateRequest(
        ApplicationIdentityObservation observation,
        ApplicationIdentityResolutionBasis basis)
    {
        if (!observation.HasResolvableEvidence)
        {
            throw new ArgumentException(
                "Application identity creation requires AUMID or executable path evidence.",
                nameof(observation));
        }

        switch (basis)
        {
            case ApplicationIdentityResolutionBasis.PackagedApplicationUserModelId:
                if (string.IsNullOrWhiteSpace(observation.ApplicationUserModelId))
                {
                    throw new ArgumentException(
                        "Packaged identity creation requires an ApplicationUserModelId.",
                        nameof(observation));
                }
                break;

            case ApplicationIdentityResolutionBasis.ExecutablePathAlias:
                if (!string.IsNullOrWhiteSpace(observation.ApplicationUserModelId) ||
                    string.IsNullOrWhiteSpace(observation.ExecutablePath))
                {
                    throw new ArgumentException(
                        "Executable-path identity creation requires path-only evidence.",
                        nameof(observation));
                }
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(basis));
        }
    }

    private static ApplicationIdentityConflictException CreateConflict(
        ApplicationIdentityObservation observation,
        ApplicationIdentityAliasLookup lookup)
    {
        return new ApplicationIdentityConflictException(
            observation,
            lookup.ApplicationUserModelIdApplicationId,
            lookup.ExecutablePathApplicationId);
    }

    private sealed class ApplicationIdentitySummaryBuilder
    {
        public ApplicationIdentitySummaryBuilder(
            ApplicationId applicationId,
            DateTimeOffset createdAtUtc)
        {
            ApplicationId = applicationId;
            CreatedAtUtc = createdAtUtc;
        }

        public ApplicationId ApplicationId { get; }

        public DateTimeOffset CreatedAtUtc { get; }

        public List<string> ApplicationUserModelIds { get; } = [];

        public List<string> ExecutablePaths { get; } = [];
    }
}
