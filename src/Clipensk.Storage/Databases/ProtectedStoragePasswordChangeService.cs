using Clipensk.Core.Security;
using Clipensk.Core.Storage;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Databases;

public enum PasswordChangeRefusal
{
    NoDatabases,

    /// <summary>Traces of an unfinished operation: staging entries, journals or re-encrypted copies.</summary>
    PendingOperation,

    SameKey,

    /// <summary>A database does not open with the current key or does not belong to this storage.</summary>
    DatabaseRefused,

    /// <summary>A database is open elsewhere and cannot be held exclusively.</summary>
    DatabaseBusy,

    InsufficientSpace,
}

public sealed class PasswordChangeRefusedException : Exception
{
    public PasswordChangeRefusedException(PasswordChangeRefusal refusal, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Refusal = refusal;
    }

    public PasswordChangeRefusal Refusal { get; }
}

public sealed record PasswordChangeProgress(int DatabasesReEncrypted, int DatabaseCount);

public sealed record PasswordChangeResult(int DatabaseCount, int QuarantineCopiesDeleted);

/// <summary>
/// Re-encrypts every storage database with the key of a new password and the same salt, and
/// settles a change that was interrupted (<c>docs/PASSWORD_CHANGE_PROTOCOL.md</c>). There is no
/// marker: the state of an interrupted change is read from the databases and their copies.
/// </summary>
public sealed class ProtectedStoragePasswordChangeService
{
    private const int SqliteNotADatabase = 26;

    private static readonly string[] AbandonedLeftoverPrefixes =
    [
        ".clipensk-storage-init-",
        ".clipensk-catalog-recovery-",
        ".clipensk-catalog-replacement-",
        ".clipensk-current-restart-",
        ".clipensk-archive-create-",
        ".clipensk-write-probe-",
    ];

    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly Func<string, long> _availableFreeSpace;

    public ProtectedStoragePasswordChangeService(
        IKeyedSqliteConnectionFactory? connectionFactory = null,
        Func<string, long>? availableFreeSpace = null)
    {
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
        _availableFreeSpace = availableFreeSpace ?? DefaultAvailableFreeSpace;
    }

    public Task<PasswordChangeResult> ChangeAsync(
        string dataRootPath,
        Guid storageId,
        ReadOnlyMemory<byte> currentKey,
        ReadOnlyMemory<byte> newKey,
        IProgress<PasswordChangeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRootPath);
        if (storageId == Guid.Empty)
        {
            throw new ArgumentException("StorageId не может быть пустым.", nameof(storageId));
        }

        ReadOnlySpan<byte> currentSalt = StorageKeyMaterial.GetSalt(currentKey.Span);
        if (!currentSalt.SequenceEqual(StorageKeyMaterial.GetSalt(newKey.Span)))
        {
            throw new ArgumentException("Новый ключ должен быть выведен с той же солью хранилища.", nameof(newKey));
        }

        return Task.Run(
            () => ChangeCore(Path.GetFullPath(dataRootPath), storageId, currentKey, newKey, progress, cancellationToken),
            cancellationToken);
    }

    /// <summary>§5: settles an interrupted change with the key the entered password gave.</summary>
    public Task<PasswordChangeRecoveryOutcome> ResolvePendingAsync(
        string dataRootPath,
        ReadOnlyMemory<byte> storageKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRootPath);
        _ = StorageKeyMaterial.GetSalt(storageKey.Span);
        return Task.Run(
            () => ResolvePendingCore(Path.GetFullPath(dataRootPath), storageKey, cancellationToken),
            cancellationToken);
    }

    private PasswordChangeResult ChangeCore(
        string root,
        Guid storageId,
        ReadOnlyMemory<byte> currentKey,
        ReadOnlyMemory<byte> newKey,
        IProgress<PasswordChangeProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (StorageKeyMaterial.GetMasterKey(currentKey.Span).SequenceEqual(StorageKeyMaterial.GetMasterKey(newKey.Span)))
        {
            throw Refused(PasswordChangeRefusal.SameKey, "The new password gives the same key as the current one.");
        }

        IReadOnlyList<string> databases = StorageDatabaseFiles.EnumerateExisting(root);
        if (databases.Count == 0)
        {
            throw Refused(PasswordChangeRefusal.NoDatabases, "The data root holds no storage database.");
        }

        PrepareNothingPending(root, databases);

        var copies = new List<string>(databases.Count);
        var held = new List<FileStream>(databases.Count);
        try
        {
            // Held exclusively for the whole of phase 1: nothing still finishing after the lock
            // can change a database between its copy and the switch.
            foreach (string database in databases)
            {
                held.Add(OpenExclusively(database));
            }

            long required = held.Sum(static stream => stream.Length);
            if (required > _availableFreeSpace(root))
            {
                throw Refused(PasswordChangeRefusal.InsufficientSpace, "The volume has no room for a copy of every database.");
            }

            for (int index = 0; index < databases.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string copy = databases[index] + StorageDatabaseFiles.PasswordChangeCopySuffix;
                copies.Add(copy);
                using (var target = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    held[index].Position = 0;
                    held[index].CopyTo(target);
                    target.Flush(flushToDisk: true);
                }

                ReEncryptCopy(copy, RoleOf(root, databases[index]), storageId, currentKey, newKey);
                progress?.Report(new PasswordChangeProgress(index + 1, databases.Count));
            }

            // An operation that was already past its last cancellation check when Clipensk locked
            // could still publish a database; one not held here would keep the old key.
            if (!StorageDatabaseFiles.EnumerateExisting(root).SequenceEqual(databases, StringComparer.OrdinalIgnoreCase))
            {
                throw Refused(PasswordChangeRefusal.DatabaseBusy, "The storage changed while the copies were made.");
            }
        }
        catch
        {
            // Phase 1 never touched a database: removing the copies is the whole rollback.
            DisposeAll(held);
            DeleteCopies(copies);
            throw;
        }

        DisposeAll(held);

        // Phase 2: every database has a verified copy; no cancellation from here on.
        int quarantineDeleted = Switch(root, databases.Select(database => (database, database + StorageDatabaseFiles.PasswordChangeCopySuffix)).ToList());
        return new PasswordChangeResult(databases.Count, quarantineDeleted);
    }

    private PasswordChangeRecoveryOutcome ResolvePendingCore(
        string root,
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> copies = StorageDatabaseFiles.EnumeratePasswordChangeCopies(root);
        if (copies.Count == 0)
        {
            return PasswordChangeRecoveryOutcome.NothingPending;
        }

        IReadOnlyList<string> databases = StorageDatabaseFiles.EnumerateExisting(root);
        var switches = new List<(string Database, string Copy)>();
        bool anyOriginalOpens = false;
        bool everyDatabaseReachable = true;
        foreach (string database in databases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DatabaseRole role = RoleOf(root, database);
            if (Opens(database, role, key))
            {
                anyOriginalOpens = true;
                continue;
            }

            string copy = database + StorageDatabaseFiles.PasswordChangeCopySuffix;
            if (File.Exists(copy) && OpensCopy(copy, role, key))
            {
                switches.Add((database, copy));
            }
            else
            {
                everyDatabaseReachable = false;
            }
        }

        // The change never removes a database, so one that is gone was lost some other way. Its
        // copy, if it is a whole database under this key, is put back where the database was;
        // any other copy of a lost database may be all that is left of it and stays as it is.
        var restores = new List<(string Database, string Copy)>();
        foreach (string copy in copies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string database = copy[..^StorageDatabaseFiles.PasswordChangeCopySuffix.Length];
            if (!File.Exists(database) && OpensCopy(copy, RoleOf(root, database), key))
            {
                restores.Add((database, copy));
            }
        }

        if (switches.Count == 0 && everyDatabaseReachable)
        {
            // The key opens every database: nothing was switched, or everything was. A lost Current
            // whose copy this key does not open is the exception: the copy may be under the other
            // password, and only with the other copies can that one finish the change and put
            // Current back. They all stay; without Current no session starts to outdate them.
            string currentPath = Path.Combine(root, StorageDatabaseFiles.CurrentRelativePath);
            bool lostCurrentCopyKept =
                !File.Exists(currentPath) &&
                File.Exists(currentPath + StorageDatabaseFiles.PasswordChangeCopySuffix) &&
                !restores.Any(entry => string.Equals(entry.Database, currentPath, StringComparison.OrdinalIgnoreCase));
            if (!lostCurrentCopyKept)
            {
                DeleteCopies(CopiesBesideTheirDatabase(copies));
            }

            foreach ((string database, string copy) in restores)
            {
                File.Move(copy, database, overwrite: false);
            }

            return PasswordChangeRecoveryOutcome.RolledBack;
        }

        if (!everyDatabaseReachable)
        {
            return anyOriginalOpens
                ? PasswordChangeRecoveryOutcome.NewPasswordRequired
                : PasswordChangeRecoveryOutcome.OldPasswordStillValid;
        }

        // Copies of databases that already open with this key are stale: never switched in.
        DeleteCopies(CopiesBesideTheirDatabase(copies)
            .Except(switches.Select(static entry => entry.Copy), StringComparer.OrdinalIgnoreCase)
            .ToList());
        Switch(root, switches.Concat(restores).ToList());
        return PasswordChangeRecoveryOutcome.Completed;
    }

    /// <summary>
    /// Phase 2: archives, then the Catalog, then the Catalog quarantine is deleted, then Current
    /// last — so an interruption before the very end still leaves a copy to resume from.
    /// </summary>
    private static int Switch(string root, IReadOnlyList<(string Database, string Copy)> switches)
    {
        string currentPath = Path.Combine(root, StorageDatabaseFiles.CurrentRelativePath);
        foreach ((string database, string copy) in switches.Where(entry =>
                     !string.Equals(entry.Database, currentPath, StringComparison.OrdinalIgnoreCase)))
        {
            File.Move(copy, database, overwrite: true);
        }

        int quarantineDeleted = DeleteCatalogQuarantine(root);
        foreach ((string database, string copy) in switches.Where(entry =>
                     string.Equals(entry.Database, currentPath, StringComparison.OrdinalIgnoreCase)))
        {
            File.Move(copy, database, overwrite: true);
        }

        return quarantineDeleted;
    }

    private void ReEncryptCopy(
        string copy,
        DatabaseRole role,
        Guid storageId,
        ReadOnlyMemory<byte> currentKey,
        ReadOnlyMemory<byte> newKey)
    {
        try
        {
            using (SqliteConnection connection = _connectionFactory.Open(copy, currentKey, SqliteOpenMode.ReadWrite))
            {
                ValidateIdentity(connection, role, storageId);
                _connectionFactory.Rekey(connection, newKey);
            }

            using (SqliteConnection connection = _connectionFactory.Open(copy, newKey, SqliteOpenMode.ReadOnly))
            {
                ValidateIdentity(connection, role, storageId);
            }
        }
        catch (Exception exception) when (exception is SqliteException or InvalidDataException)
        {
            throw Refused(
                PasswordChangeRefusal.DatabaseRefused,
                $"'{Path.GetFileName(copy)}' does not open with the current key or is not a database of this storage.",
                exception);
        }
        finally
        {
            DeleteIfExists(copy + "-journal");
        }
    }

    /// <summary>
    /// Whether a database opens with the key. Any other failure than a refused key is not a
    /// question of which password applies, and stops the resolution.
    /// </summary>
    private bool Opens(string path, DatabaseRole role, ReadOnlyMemory<byte> key)
    {
        try
        {
            using SqliteConnection connection = _connectionFactory.Open(path, key, SqliteOpenMode.ReadOnly);
            ValidateIdentity(connection, role, storageId: null);
            return true;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == SqliteNotADatabase)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a copy is a finished re-encryption under the key. A copy cut short while it was
    /// written or re-encrypted is simply not one, whatever error opening it gives.
    /// </summary>
    private bool OpensCopy(string copy, DatabaseRole role, ReadOnlyMemory<byte> key)
    {
        try
        {
            using SqliteConnection connection = _connectionFactory.Open(copy, key, SqliteOpenMode.ReadOnly);
            ValidateIdentity(connection, role, storageId: null);
            return true;
        }
        catch (Exception exception) when (exception is SqliteException or InvalidDataException)
        {
            return false;
        }
    }

    private static void ValidateIdentity(SqliteConnection connection, DatabaseRole role, Guid? storageId)
    {
        using (SqliteCommand quickCheck = connection.CreateCommand())
        {
            quickCheck.CommandText = "PRAGMA quick_check;";
            if (!string.Equals(quickCheck.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("SQLite quick_check did not confirm the database.");
            }
        }

        using SqliteCommand identity = connection.CreateCommand();
        identity.CommandText = "SELECT StorageId, DatabaseRole FROM DatabaseIdentity WHERE SingletonId = 1;";
        using SqliteDataReader reader = identity.ExecuteReader();
        if (!reader.Read() ||
            !Guid.TryParse(reader.GetString(0), out Guid actualStorageId) ||
            (storageId is Guid expected && actualStorageId != expected) ||
            !string.Equals(reader.GetString(1), role.ToString(), StringComparison.Ordinal))
        {
            throw new InvalidDataException("DatabaseIdentity does not match the database's place in the storage.");
        }
    }

    private static DatabaseRole RoleOf(string root, string database)
    {
        string relative = Path.GetRelativePath(root, database);
        if (string.Equals(relative, StorageDatabaseFiles.CurrentRelativePath, StringComparison.OrdinalIgnoreCase))
        {
            return DatabaseRole.Current;
        }

        return string.Equals(relative, StorageDatabaseFiles.CatalogRelativePath, StringComparison.OrdinalIgnoreCase)
            ? DatabaseRole.StorageCatalog
            : DatabaseRole.Archive;
    }

    /// <summary>
    /// Staging of an Archive rotation or split belongs to an operation the next unlock resumes with
    /// the current key: the change waits for it. Leftovers of operations that run to completion or
    /// not at all (a crashed storage initialization, Catalog recovery or replacement, Current
    /// restart, archive creation, write probe) are nobody's any more and are removed — they would
    /// otherwise stay readable with the old password. Anything else is refused.
    /// </summary>
    private static void PrepareNothingPending(string root, IReadOnlyList<string> databases)
    {
        foreach (string directory in new[]
                 {
                     root,
                     Path.Combine(root, StorageDatabaseFiles.CurrentDirectoryName),
                     Path.Combine(root, StorageDatabaseFiles.ArchiveDirectoryName),
                 })
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (FileSystemInfo entry in new DirectoryInfo(directory).EnumerateFileSystemInfos(".clipensk-*"))
            {
                bool abandoned = AbandonedLeftoverPrefixes.Any(prefix =>
                    entry.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                if (!abandoned || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw Refused(PasswordChangeRefusal.PendingOperation, $"'{entry.FullName}' belongs to an unfinished operation.");
                }

                try
                {
                    if (entry is DirectoryInfo leftoverDirectory)
                    {
                        leftoverDirectory.Delete(recursive: true);
                    }
                    else
                    {
                        entry.Delete();
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Still open: the operation that made it has not finished after the lock yet.
                    throw Refused(PasswordChangeRefusal.DatabaseBusy, $"'{entry.FullName}' is in use.", exception);
                }
            }
        }

        if (StorageDatabaseFiles.EnumeratePasswordChangeCopies(root).Count > 0)
        {
            throw Refused(PasswordChangeRefusal.PendingOperation, "An earlier password change was not finished.");
        }

        // A journal is an interrupted write the next open would replay; it belongs to the current key.
        if (databases.Any(static database => File.Exists(database + "-journal") || File.Exists(database + "-wal")))
        {
            throw Refused(PasswordChangeRefusal.PendingOperation, "A database has an unfinished write.");
        }
    }

    private static FileStream OpenExclusively(string database)
    {
        try
        {
            return new FileStream(database, FileMode.Open, FileAccess.Read, FileShare.None, bufferSize: 0, FileOptions.SequentialScan);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Refused(PasswordChangeRefusal.DatabaseBusy, $"'{Path.GetFileName(database)}' is in use.", exception);
        }
    }

    private static int DeleteCatalogQuarantine(string root)
    {
        string quarantine = Path.Combine(root, StorageDatabaseFiles.CurrentDirectoryName, CatalogQuarantineFileName.DirectoryName);
        if (!Directory.Exists(quarantine))
        {
            return 0;
        }

        int deleted = 0;
        foreach (FileInfo file in new DirectoryInfo(quarantine).EnumerateFiles())
        {
            if (!file.Attributes.HasFlag(FileAttributes.ReparsePoint) &&
                CatalogQuarantineFileName.TryParse(file.Name, out _))
            {
                file.Delete();
                deleted++;
            }
        }

        return deleted;
    }

    /// <summary>
    /// Only a copy whose database is still there is a leftover. One whose database is gone — the
    /// change never removes a database — may be the only one left of it and is not deleted.
    /// </summary>
    private static IReadOnlyList<string> CopiesBesideTheirDatabase(IReadOnlyList<string> copies) =>
        copies
            .Where(static copy => File.Exists(copy[..^StorageDatabaseFiles.PasswordChangeCopySuffix.Length]))
            .ToList();

    private static void DeleteCopies(IReadOnlyList<string> copies)
    {
        foreach (string copy in copies)
        {
            DeleteIfExists(copy);
            DeleteIfExists(copy + "-journal");
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void DisposeAll(List<FileStream> streams)
    {
        foreach (FileStream stream in streams)
        {
            stream.Dispose();
        }

        streams.Clear();
    }

    private static long DefaultAvailableFreeSpace(string root) =>
        new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace;

    private static PasswordChangeRefusedException Refused(
        PasswordChangeRefusal refusal,
        string message,
        Exception? innerException = null) =>
        new(refusal, message, innerException);
}
