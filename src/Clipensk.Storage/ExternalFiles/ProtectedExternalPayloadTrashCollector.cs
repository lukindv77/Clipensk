using System.Globalization;
using System.Security.Cryptography;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.ExternalFiles;

public sealed record ExternalPayloadTrashCollectionResult(
    int CollectedFileCount,
    IReadOnlyList<string> TrashRelativePaths);

public sealed class ProtectedExternalPayloadTrashCollector
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly string _dataRootPath;
    private readonly string _catalogDatabasePath;
    private readonly string _filesRootPath;
    private readonly string _filesRootPrefix;
    private readonly string _trashRootPath;
    private readonly string _trashRootPrefix;

    public ProtectedExternalPayloadTrashCollector(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();

        _dataRootPath = Path.GetFullPath(session.DataRootPath);
        _catalogDatabasePath = Path.Combine(_dataRootPath, "Current", "storage-catalog.db");
        _filesRootPath = Path.TrimEndingDirectorySeparator(Path.Combine(_dataRootPath, "Files"));
        _filesRootPrefix = _filesRootPath + Path.DirectorySeparatorChar;
        _trashRootPath = Path.TrimEndingDirectorySeparator(Path.Combine(_dataRootPath, "Trash"));
        _trashRootPrefix = _trashRootPath + Path.DirectorySeparatorChar;
    }

    public async Task<ExternalPayloadTrashCollectionResult> CollectAsync(
        DateOnly deletionDate,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                _session.CancellationToken,
                cancellationToken);
        CancellationToken token = linkedCancellation.Token;
        token.ThrowIfCancellationRequested();

        // History is authoritative. Rebuild first so stale capture reservations do not
        // keep orphan files forever merely because the rebuildable Catalog still lists them.
        var rebuild = new ProtectedExternalPayloadCatalogRebuildService(
            _session,
            _connectionFactory);
        await rebuild.RebuildAsync(token).ConfigureAwait(false);

        // A capture may complete between the rebuild and this lease acquisition. Capture
        // reserves its Catalog address while holding the same session mutation lease, so
        // the post-lease Catalog read below is a conservative race-safe keep-set. A capture
        // crash may leave a new stale reservation here; keeping that file only defers GC
        // until a later run and cannot delete live content.
        using ProtectedStorageMutationLease mutationLease =
            await _session.AcquireMutationLeaseAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        return await Task.Run(
            () => CollectUnderMutationLease(deletionDate, token),
            CancellationToken.None).ConfigureAwait(false);
    }

    private ExternalPayloadTrashCollectionResult CollectUnderMutationLease(
        DateOnly deletionDate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureActiveSession();

        Dictionary<string, ExternalPayloadAddress> keepByRelativePath =
            ReadCatalogKeepSet(cancellationToken);
        ManagedExternalFile[] managedFiles = EnumerateManagedFiles(cancellationToken);

        var plans = new List<CollectionPlan>();
        foreach (ManagedExternalFile managedFile in managedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (keepByRelativePath.ContainsKey(managedFile.RelativePath))
            {
                continue;
            }

            ValidateManagedSourcePath(managedFile.FullPath);
            FileFingerprint sourceFingerprint = ReadAndValidateFingerprint(
                managedFile.FullPath,
                managedFile.Sha256);
            string destinationPath = GetTrashDestinationPath(
                deletionDate,
                managedFile.RelativePath);
            ValidateExistingTrashPath(destinationPath);

            if (File.Exists(destinationPath))
            {
                FileFingerprint destinationFingerprint = ReadAndValidateFingerprint(
                    destinationPath,
                    managedFile.Sha256);
                if (destinationFingerprint.SizeBytes != sourceFingerprint.SizeBytes)
                {
                    throw new InvalidDataException(
                        "Existing Trash object conflicts with the orphan external payload.");
                }
            }

            plans.Add(new CollectionPlan(
                managedFile,
                sourceFingerprint,
                destinationPath));
        }

        var collectedRelativePaths = new List<string>(plans.Count);
        foreach (CollectionPlan plan in plans)
        {
            // Each filesystem move/delete is its own durable publication boundary.
            // Never check cancellation immediately after a successful publication.
            cancellationToken.ThrowIfCancellationRequested();
            EnsureActiveSession();

            ValidateManagedSourcePath(plan.Source.FullPath);
            FileFingerprint currentSource = ReadAndValidateFingerprint(
                plan.Source.FullPath,
                plan.Source.Sha256);
            if (currentSource != plan.SourceFingerprint)
            {
                throw new InvalidDataException(
                    "External payload file changed after Trash GC preflight.");
            }

            string? destinationDirectory = Path.GetDirectoryName(plan.DestinationPath);
            if (string.IsNullOrEmpty(destinationDirectory))
            {
                throw new InvalidDataException("Trash destination directory is invalid.");
            }
            EnsureSafeTrashDirectory(destinationDirectory);
            ValidateExistingTrashPath(plan.DestinationPath);

            cancellationToken.ThrowIfCancellationRequested();
            EnsureActiveSession();

            if (File.Exists(plan.DestinationPath))
            {
                FileFingerprint destinationFingerprint = ReadAndValidateFingerprint(
                    plan.DestinationPath,
                    plan.Source.Sha256);
                if (destinationFingerprint.SizeBytes != currentSource.SizeBytes)
                {
                    throw new InvalidDataException(
                        "Existing Trash object changed and conflicts with the orphan external payload.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                ValidateManagedSourcePath(plan.Source.FullPath);
                ValidateExistingTrashPath(plan.DestinationPath);
                File.Delete(plan.Source.FullPath);
            }
            else
            {
                ValidateManagedSourcePath(plan.Source.FullPath);
                ValidateExistingTrashPath(plan.DestinationPath);
                File.Move(plan.Source.FullPath, plan.DestinationPath);
            }

            collectedRelativePaths.Add(Path.GetRelativePath(
                _dataRootPath,
                plan.DestinationPath));
        }

        return new ExternalPayloadTrashCollectionResult(
            collectedRelativePaths.Count,
            collectedRelativePaths.AsReadOnly());
    }

    private Dictionary<string, ExternalPayloadAddress> ReadCatalogKeepSet(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureActiveSession();

        using SqliteConnection connection = _connectionFactory.Open(
            _catalogDatabasePath,
            _session.DangerousGetMasterKeyMemory(),
            SqliteOpenMode.ReadOnly);
        ValidateCatalogDatabase(connection, cancellationToken);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT Sha256, RelativePath, SizeBytes
            FROM ExternalPayloadAddressIndex
            ORDER BY Sha256 COLLATE BINARY;
            """;

        var keepByRelativePath = new Dictionary<string, ExternalPayloadAddress>(
            StringComparer.OrdinalIgnoreCase);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var address = new ExternalPayloadAddress(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2));
            ValidatePersistedAddress(address);

            if (keepByRelativePath.TryGetValue(
                    address.RelativePath,
                    out ExternalPayloadAddress? existing) &&
                existing != address)
            {
                throw new InvalidDataException(
                    "Catalog contains conflicting external payload paths during Trash GC.");
            }

            keepByRelativePath[address.RelativePath] = address;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return keepByRelativePath;
    }

    private ManagedExternalFile[] EnumerateManagedFiles(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_filesRootPath))
        {
            return [];
        }
        EnsurePathIsNotReparsePoint(
            _filesRootPath,
            "Configured Files root is a reparse point during Trash GC.");

        var result = new List<ManagedExternalFile>();
        foreach (string dateDirectoryPath in Directory.EnumerateDirectories(
                     _filesRootPath,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string dateDirectoryName = Path.GetFileName(dateDirectoryPath);
            if (!DateOnly.TryParseExact(
                    dateDirectoryName,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateOnly storedDate) ||
                !string.Equals(
                    dateDirectoryName,
                    storedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    StringComparison.Ordinal))
            {
                continue;
            }

            EnsurePathIsNotReparsePoint(
                dateDirectoryPath,
                "Canonical Files date directory is a reparse point during Trash GC.");

            foreach (string filePath in Directory.EnumerateFiles(
                         dateDirectoryPath,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string fileName = Path.GetFileName(filePath);
                if (!TryParseCanonicalManagedFileName(fileName, out string sha256))
                {
                    continue;
                }

                string relativePath = Path.Combine(dateDirectoryName, fileName);
                string fullPath = Path.GetFullPath(filePath);
                if (!fullPath.StartsWith(_filesRootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "Managed external payload candidate escapes the configured Files root.");
                }

                ValidateManagedSourcePath(fullPath);
                result.Add(new ManagedExternalFile(relativePath, fullPath, sha256));
            }
        }

        return result
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TryParseCanonicalManagedFileName(
        string fileName,
        out string sha256)
    {
        sha256 = string.Empty;
        if (fileName.Length <= 65 || fileName[64] != '.')
        {
            return false;
        }

        string candidateSha = fileName[..64];
        if (candidateSha.Any(character =>
                !((character >= '0' && character <= '9') ||
                  (character >= 'a' && character <= 'f'))))
        {
            return false;
        }

        string extension = fileName[64..];
        if (extension.Length <= 1 ||
            !string.Equals(extension, extension.ToLowerInvariant(), StringComparison.Ordinal))
        {
            return false;
        }

        sha256 = candidateSha;
        return true;
    }

    private string GetTrashDestinationPath(
        DateOnly deletionDate,
        string filesRelativePath)
    {
        string deletionDirectoryName = deletionDate.ToString(
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture);
        string candidate = Path.GetFullPath(Path.Combine(
            _trashRootPath,
            deletionDirectoryName,
            filesRelativePath));
        string deletionRoot = Path.TrimEndingDirectorySeparator(Path.Combine(
            _trashRootPath,
            deletionDirectoryName));
        string deletionRootPrefix = deletionRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(deletionRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Trash destination escapes its deletion-date root.");
        }
        return candidate;
    }

    private void ValidateManagedSourcePath(string fullPath)
    {
        if (!fullPath.StartsWith(_filesRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Managed external payload path escapes the configured Files root.");
        }

        EnsurePathIsNotReparsePoint(
            _filesRootPath,
            "Configured Files root is a reparse point during Trash GC.");

        string? parentDirectory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(parentDirectory) ||
            !parentDirectory.StartsWith(_filesRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Managed external payload parent directory escapes the configured Files root.");
        }

        EnsurePathIsNotReparsePoint(
            parentDirectory,
            "Managed external payload parent directory is a reparse point during Trash GC.");
        EnsurePathIsNotReparsePoint(
            fullPath,
            "Managed external payload file is a reparse point during Trash GC.");
    }

    private void ValidateExistingTrashPath(string fullPath)
    {
        if (!fullPath.StartsWith(_trashRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Trash destination escapes the configured Trash root.");
        }

        if (Directory.Exists(_trashRootPath) || File.Exists(_trashRootPath))
        {
            EnsurePathIsNotReparsePoint(
                _trashRootPath,
                "Configured Trash root is a reparse point during external payload GC.");
        }

        string relativePath = Path.GetRelativePath(_trashRootPath, fullPath);
        string currentPath = _trashRootPath;
        foreach (string part in SplitRelativePath(relativePath))
        {
            currentPath = Path.Combine(currentPath, part);
            if (!Directory.Exists(currentPath) && !File.Exists(currentPath))
            {
                break;
            }

            EnsurePathIsNotReparsePoint(
                currentPath,
                "Existing Trash path contains a reparse point during external payload GC.");
        }
    }

    private void EnsureSafeTrashDirectory(string directoryPath)
    {
        if (!directoryPath.StartsWith(_trashRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Trash directory escapes the configured Trash root.");
        }

        EnsureDirectoryExistsWithoutReparsePoint(_trashRootPath);
        string relativePath = Path.GetRelativePath(_trashRootPath, directoryPath);
        string currentPath = _trashRootPath;
        foreach (string part in SplitRelativePath(relativePath))
        {
            currentPath = Path.Combine(currentPath, part);
            EnsureDirectoryExistsWithoutReparsePoint(currentPath);
        }
    }

    private static IEnumerable<string> SplitRelativePath(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || relativePath == ".")
        {
            yield break;
        }

        foreach (string part in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "." || part == "..")
            {
                throw new InvalidDataException(
                    "Relative managed storage path contains an invalid traversal component.");
            }
            yield return part;
        }
    }

    private static void EnsureDirectoryExistsWithoutReparsePoint(string path)
    {
        if (File.Exists(path) && !Directory.Exists(path))
        {
            throw new InvalidDataException(
                "Managed Trash directory path is occupied by a file.");
        }

        Directory.CreateDirectory(path);
        EnsurePathIsNotReparsePoint(
            path,
            "Managed Trash directory is a reparse point during external payload GC.");
    }

    private static void EnsurePathIsNotReparsePoint(
        string path,
        string errorMessage)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new InvalidDataException(
                "Managed external payload filesystem metadata could not be validated safely.",
                exception);
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(errorMessage);
        }
    }

    private FileFingerprint ReadAndValidateFingerprint(
        string path,
        string expectedSha256)
    {
        EnsurePathIsNotReparsePoint(
            path,
            "Managed external payload object is a reparse point during Trash GC.");

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        long sizeBytes = stream.Length;
        using SHA256 sha256 = SHA256.Create();
        string actualSha256 = Convert
            .ToHexString(sha256.ComputeHash(stream))
            .ToLowerInvariant();
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Canonical external payload file bytes do not match the SHA-256 in its file name.");
        }
        return new FileFingerprint(actualSha256, sizeBytes);
    }

    private void ValidatePersistedAddress(ExternalPayloadAddress address)
    {
        if (address.Sha256.Length != 64 ||
            address.Sha256.Any(character =>
                !((character >= '0' && character <= '9') ||
                  (character >= 'a' && character <= 'f'))) ||
            address.SizeBytes < 0 ||
            string.IsNullOrWhiteSpace(address.RelativePath) ||
            Path.IsPathRooted(address.RelativePath))
        {
            throw new InvalidDataException(
                "Catalog contains invalid external payload address metadata during Trash GC.");
        }

        string candidatePath;
        try
        {
            candidatePath = Path.GetFullPath(Path.Combine(
                _filesRootPath,
                address.RelativePath));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException(
                "Catalog external payload relative path is invalid during Trash GC.",
                exception);
        }

        if (!candidatePath.StartsWith(_filesRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Catalog external payload address escapes the configured Files root during Trash GC.");
        }
    }

    private void ValidateCatalogDatabase(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        int schemaVersion;
        using (SqliteCommand identity = connection.CreateCommand())
        {
            identity.CommandText = """
                SELECT StorageId, DatabaseRole, SchemaVersion
                FROM DatabaseIdentity
                WHERE SingletonId = 1;
                """;
            using SqliteDataReader reader = identity.ExecuteReader();
            if (!reader.Read() ||
                !Guid.TryParseExact(reader.GetString(0), "D", out Guid storageId) ||
                storageId != _session.StorageId ||
                !string.Equals(
                    reader.GetString(1),
                    DatabaseRole.StorageCatalog.ToString(),
                    StringComparison.Ordinal) ||
                reader.GetInt32(2) < ArchiveSegmentCatalogSqlSchema.RequiredCatalogSchemaVersion)
            {
                throw new InvalidDataException(
                    "External payload Trash GC requires the expected StorageCatalog v3 or later.");
            }
            schemaVersion = reader.GetInt32(2);
        }

        using (SqliteCommand userVersion = connection.CreateCommand())
        {
            userVersion.CommandText = "PRAGMA user_version;";
            if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) != schemaVersion)
            {
                throw new InvalidDataException(
                    "StorageCatalog user_version does not match DatabaseIdentity during Trash GC.");
            }
        }

        ExternalPayloadCatalogSqlSchema.ValidateTables(connection);
        ArchiveSegmentCatalogSqlSchema.ValidateTable(connection);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void EnsureActiveSession()
    {
        if (!_session.IsActive)
        {
            throw new OperationCanceledException(_session.CancellationToken);
        }
    }

    private sealed record ManagedExternalFile(
        string RelativePath,
        string FullPath,
        string Sha256);

    private sealed record CollectionPlan(
        ManagedExternalFile Source,
        FileFingerprint SourceFingerprint,
        string DestinationPath);

    private sealed record FileFingerprint(
        string Sha256,
        long SizeBytes);
}
