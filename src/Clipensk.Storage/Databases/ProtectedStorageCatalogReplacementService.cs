using System.Security.Cryptography;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Databases;

/// <summary>
/// Explicit pre-session replacement for an existing rebuildable storage Catalog.
/// The replacement is fully derived and validated before the existing Catalog is
/// atomically replaced and preserved as a quarantine backup.
/// </summary>
public sealed class ProtectedStorageCatalogReplacementService
{
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;

    public ProtectedStorageCatalogReplacementService(
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
    }

    public Task<ProtectedStorageCatalogReplacementResult> ReplaceExistingCatalogAsync(
        string dataRootPath,
        Guid storageId,
        ReadOnlyMemory<byte> masterKey,
        DateOnly currentCalendarDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRootPath);
        if (storageId == Guid.Empty)
        {
            throw new ArgumentException("StorageId не может быть пустым.", nameof(storageId));
        }
        if (masterKey.Length != 32)
        {
            throw new ArgumentException("MasterKey должен содержать 32 байта.", nameof(masterKey));
        }

        string root = Path.GetFullPath(dataRootPath);
        return Task.Run(
            () => ReplaceCore(
                root,
                storageId,
                masterKey,
                currentCalendarDate,
                cancellationToken),
            cancellationToken);
    }

    private ProtectedStorageCatalogReplacementResult ReplaceCore(
        string dataRootPath,
        Guid storageId,
        ReadOnlyMemory<byte> masterKey,
        DateOnly currentCalendarDate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(dataRootPath))
        {
            return Failure(ProtectedStorageDatabaseStatus.StorageFailure);
        }

        string currentDirectory = Path.Combine(dataRootPath, "Current");
        string currentPath = Path.Combine(currentDirectory, "current.db");
        string catalogPath = Path.Combine(currentDirectory, "storage-catalog.db");
        if (!File.Exists(currentPath) || !File.Exists(catalogPath))
        {
            return Failure(ProtectedStorageDatabaseStatus.MissingOrPartialStorage);
        }

        string originalCatalogHash;
        try
        {
            originalCatalogHash = ComputeFileSha256(catalogPath, cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return Failure(ProtectedStorageDatabaseStatus.StorageFailure);
        }

        string[] originalArchiveNames = EnumerateArchiveNames(dataRootPath);
        string shadowRoot = Path.Combine(
            dataRootPath,
            $".clipensk-catalog-replacement-{Guid.NewGuid():N}");
        string shadowCurrentDirectory = Path.Combine(shadowRoot, "Current");
        string shadowArchiveDirectory = Path.Combine(shadowRoot, "Archive");
        string shadowFilesDirectory = Path.Combine(shadowRoot, "Files");
        string shadowCurrentPath = Path.Combine(shadowCurrentDirectory, "current.db");
        string shadowCatalogPath = Path.Combine(
            shadowCurrentDirectory,
            "storage-catalog.db");

        try
        {
            Directory.CreateDirectory(shadowCurrentDirectory);
            Directory.CreateDirectory(shadowArchiveDirectory);
            Directory.CreateDirectory(shadowFilesDirectory);

            var routes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [Path.GetFullPath(shadowCurrentPath)] = Path.GetFullPath(currentPath),
            };
            CreatePlaceholder(shadowCurrentPath);

            string sourceArchiveDirectory = Path.Combine(dataRootPath, "Archive");
            foreach (string archiveName in originalArchiveNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string aliasPath = Path.Combine(shadowArchiveDirectory, archiveName);
                string sourcePath = Path.Combine(sourceArchiveDirectory, archiveName);
                CreatePlaceholder(aliasPath);
                routes[Path.GetFullPath(aliasPath)] = Path.GetFullPath(sourcePath);
            }

            var routingFactory = new ReadOnlySourceRoutingConnectionFactory(
                _connectionFactory,
                routes);
            var recovery = new ProtectedStorageCatalogRecoveryService(routingFactory);
            ProtectedStorageDatabaseResult recoveryResult = recovery
                .RecoverMissingCatalogAsync(
                    shadowRoot,
                    storageId,
                    masterKey,
                    currentCalendarDate,
                    cancellationToken)
                .GetAwaiter()
                .GetResult();

            if (!recoveryResult.IsSuccess)
            {
                return Failure(recoveryResult.Status);
            }
            if (!File.Exists(shadowCatalogPath))
            {
                return Failure(ProtectedStorageDatabaseStatus.StorageFailure);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(currentPath) || !File.Exists(catalogPath))
            {
                return Failure(ProtectedStorageDatabaseStatus.MissingOrPartialStorage);
            }

            string[] finalArchiveNames = EnumerateArchiveNames(dataRootPath);
            if (!originalArchiveNames.SequenceEqual(finalArchiveNames, StringComparer.Ordinal))
            {
                return Failure(ProtectedStorageDatabaseStatus.MissingOrPartialStorage);
            }

            string finalCatalogHash = ComputeFileSha256(catalogPath, cancellationToken);
            if (!string.Equals(
                    originalCatalogHash,
                    finalCatalogHash,
                    StringComparison.Ordinal))
            {
                return Failure(ProtectedStorageDatabaseStatus.MissingOrPartialStorage);
            }

            string quarantineDirectory = Path.Combine(
                currentDirectory,
                "CatalogQuarantine");
            Directory.CreateDirectory(quarantineDirectory);
            string quarantineFileName =
                $"storage-catalog-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}.db";
            string quarantinePath = Path.Combine(
                quarantineDirectory,
                quarantineFileName);

            cancellationToken.ThrowIfCancellationRequested();
            File.Replace(
                shadowCatalogPath,
                catalogPath,
                quarantinePath,
                ignoreMetadataErrors: false);

            // No cancellation check after atomic publication. A fully built and
            // validated replacement is now durable and the previous Catalog is
            // preserved as the quarantine backup.
            return new ProtectedStorageCatalogReplacementResult(
                ProtectedStorageDatabaseStatus.Success,
                Path.GetRelativePath(dataRootPath, quarantinePath));
        }
        catch (ProtectedStorageEncryptionUnavailableException)
        {
            return Failure(ProtectedStorageDatabaseStatus.EncryptionEngineUnavailable);
        }
        catch (SqliteException)
        {
            return Failure(ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity);
        }
        catch (InvalidDataException)
        {
            return Failure(ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return Failure(ProtectedStorageDatabaseStatus.StorageFailure);
        }
        finally
        {
            TryDeleteDirectory(shadowRoot);
        }
    }

    private static ProtectedStorageCatalogReplacementResult Failure(
        ProtectedStorageDatabaseStatus status) =>
        new(status, QuarantineRelativePath: null);

    private static void CreatePlaceholder(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read);
    }

    private static string[] EnumerateArchiveNames(string dataRootPath)
    {
        string archiveDirectory = Path.Combine(dataRootPath, "Archive");
        if (!Directory.Exists(archiveDirectory))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(
                archiveDirectory,
                "archive_*.db",
                SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static string ComputeFileSha256(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }
            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Cleanup failure must not demote a successfully published replacement.
        }
    }

    private sealed class ReadOnlySourceRoutingConnectionFactory
        : IKeyedSqliteConnectionFactory
    {
        private readonly IKeyedSqliteConnectionFactory _inner;
        private readonly IReadOnlyDictionary<string, string> _routes;

        public ReadOnlySourceRoutingConnectionFactory(
            IKeyedSqliteConnectionFactory inner,
            IReadOnlyDictionary<string, string> routes)
        {
            _inner = inner;
            _routes = routes;
        }

        public SqliteConnection Open(
            string databasePath,
            ReadOnlyMemory<byte> masterKey,
            SqliteOpenMode mode)
        {
            string fullPath = Path.GetFullPath(databasePath);
            if (_routes.TryGetValue(fullPath, out string? sourcePath))
            {
                if (mode != SqliteOpenMode.ReadOnly)
                {
                    throw new InvalidOperationException(
                        "Catalog replacement source aliases may only be opened ReadOnly.");
                }
                return _inner.Open(sourcePath, masterKey, mode);
            }

            return _inner.Open(fullPath, masterKey, mode);
        }
    }
}

public sealed record ProtectedStorageCatalogReplacementResult(
    ProtectedStorageDatabaseStatus Status,
    string? QuarantineRelativePath)
{
    public bool IsSuccess => Status == ProtectedStorageDatabaseStatus.Success;
}
