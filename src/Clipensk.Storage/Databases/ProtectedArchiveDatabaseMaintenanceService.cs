using Clipensk.Core.Storage;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Databases;

/// <summary>
/// Runs physical SQLite maintenance for one existing Archive database without changing
/// its logical identity, assigned coverage, or storage-catalog projection.
/// </summary>
public sealed class ProtectedArchiveDatabaseMaintenanceService
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;

    public ProtectedArchiveDatabaseMaintenanceService(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
    }

    public Task<DatabaseIdentity> VacuumAsync(
        ArchiveFileName archiveFileName,
        CancellationToken cancellationToken = default) =>
        MaintainAsync(archiveFileName, "VACUUM;", cancellationToken);

    public Task<DatabaseIdentity> OptimizeAsync(
        ArchiveFileName archiveFileName,
        CancellationToken cancellationToken = default) =>
        MaintainAsync(archiveFileName, "PRAGMA optimize;", cancellationToken);

    private async Task<DatabaseIdentity> MaintainAsync(
        ArchiveFileName archiveFileName,
        string commandText,
        CancellationToken cancellationToken)
    {
        using ProtectedStorageMutationLease mutationLease =
            await _session.AcquireMutationLeaseAsync(cancellationToken)
                .ConfigureAwait(false);

        // Validate the complete self-describing Archive contract while the mutation lease is
        // already held. Invalid/corrupt targets therefore never reach a read-write open.
        var archiveService = new ProtectedArchiveDatabaseService(_session, _connectionFactory);
        DatabaseIdentity identity = await archiveService
            .ValidateAsync(archiveFileName, cancellationToken)
            .ConfigureAwait(false);

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            cancellationToken);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();

        await Task.Run(
            () => MaintainCore(archiveFileName, commandText, token),
            CancellationToken.None).ConfigureAwait(false);

        return identity;
    }

    private void MaintainCore(
        ArchiveFileName archiveFileName,
        string commandText,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string archivePath = Path.Combine(
            Path.GetFullPath(_session.DataRootPath),
            "Archive",
            archiveFileName.FileName);

        using SqliteConnection connection = _connectionFactory.Open(
            archivePath,
            _session.DangerousGetMasterKeyMemory(),
            SqliteOpenMode.ReadWrite);
        cancellationToken.ThrowIfCancellationRequested();

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = commandText;
            command.ExecuteNonQuery();
        }

        // VACUUM/PRAGMA optimize are physical maintenance operations and cannot be rolled back
        // after the SQLite command has completed. Do not demote a completed durable operation
        // because of late caller cancellation; verify the resulting database on the same keyed
        // connection before releasing the mutation lease.
        using SqliteCommand quickCheck = connection.CreateCommand();
        quickCheck.CommandText = "PRAGMA quick_check;";
        if (quickCheck.ExecuteScalar() is not string result ||
            !string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Archive SQLite quick_check failed after physical maintenance.");
        }
    }
}
