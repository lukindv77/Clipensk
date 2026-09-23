namespace Clipensk.Core.Storage;

/// <summary>
/// The database files that make a data root a storage and whose first 16 bytes carry the storage
/// salt (<c>docs/CRYPTOGRAPHY.md</c> §3): <c>Current/current.db</c>, <c>Current/storage-catalog.db</c>
/// and every <c>Archive/archive_*.db</c>, consulted in that order.
/// </summary>
public static class StorageDatabaseFiles
{
    public const string CurrentDirectoryName = "Current";
    public const string ArchiveDirectoryName = "Archive";
    public const string CurrentFileName = "current.db";
    public const string CatalogFileName = "storage-catalog.db";
    public const string ArchiveSearchPattern = "archive_*.db";

    /// <summary>
    /// The metadata file of the storage format used before 2026-09-23. A data root holding it is
    /// not supported and is never taken for an empty one (<c>docs/CRYPTOGRAPHY.md</c> §3).
    /// </summary>
    public const string LegacyCryptoMetadataFileName = "storage-crypto.json";

    /// <summary>
    /// The suffix of a database's copy re-encrypted with a new password, next to the database,
    /// while a password change is under way (<c>docs/PASSWORD_CHANGE_PROTOCOL.md</c> §4).
    /// </summary>
    public const string PasswordChangeCopySuffix = ".clipensk-rekey";

    public static string CurrentRelativePath { get; } = Path.Combine(CurrentDirectoryName, CurrentFileName);

    public static string CatalogRelativePath { get; } = Path.Combine(CurrentDirectoryName, CatalogFileName);

    public static IReadOnlyList<string> EnumerateExisting(string dataRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRootPath);
        string root = Path.GetFullPath(dataRootPath);

        var files = new List<string>();
        foreach (string relativePath in new[] { CurrentRelativePath, CatalogRelativePath })
        {
            string path = Path.Combine(root, relativePath);
            if (File.Exists(path))
            {
                files.Add(path);
            }
        }

        string archiveDirectory = Path.Combine(root, ArchiveDirectoryName);
        if (Directory.Exists(archiveDirectory))
        {
            string[] archives = Directory.GetFiles(archiveDirectory, ArchiveSearchPattern, SearchOption.TopDirectoryOnly);
            Array.Sort(archives, StringComparer.OrdinalIgnoreCase);
            files.AddRange(archives);
        }

        return files;
    }

    /// <summary>
    /// The re-encrypted copies of an interrupted password change: files named after a storage
    /// database plus <see cref="PasswordChangeCopySuffix"/>, in <c>Current</c> and <c>Archive</c>,
    /// whether or not the database itself is still there.
    /// </summary>
    public static IReadOnlyList<string> EnumeratePasswordChangeCopies(string dataRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRootPath);
        string root = Path.GetFullPath(dataRootPath);

        var copies = new List<string>();
        foreach (string directoryName in new[] { CurrentDirectoryName, ArchiveDirectoryName })
        {
            string directory = Path.Combine(root, directoryName);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (string path in Directory.GetFiles(directory, "*" + PasswordChangeCopySuffix, SearchOption.TopDirectoryOnly))
            {
                string databasePath = path[..^PasswordChangeCopySuffix.Length];
                if (IsStorageDatabase(Path.GetRelativePath(root, databasePath)))
                {
                    copies.Add(path);
                }
            }
        }

        copies.Sort(StringComparer.OrdinalIgnoreCase);
        return copies;
    }

    /// <summary>Whether a path relative to the data root names one of the storage databases.</summary>
    public static bool IsStorageDatabase(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (string.Equals(relativePath, CurrentRelativePath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(relativePath, CatalogRelativePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string fileName = Path.GetFileName(relativePath);
        return string.Equals(Path.GetDirectoryName(relativePath), ArchiveDirectoryName, StringComparison.OrdinalIgnoreCase) &&
               fileName.StartsWith("archive_", StringComparison.OrdinalIgnoreCase) &&
               fileName.EndsWith(".db", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the storage salt from the first bytes of a database file. False when the file is too
    /// short or cannot be read; the salt is not checked here.
    /// </summary>
    public static bool TryReadSalt(string databasePath, Span<byte> salt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        try
        {
            using var stream = new FileStream(
                databasePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1);
            stream.ReadExactly(salt);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // EndOfStreamException is an IOException: a file shorter than the salt carries none.
            return false;
        }
    }
}
