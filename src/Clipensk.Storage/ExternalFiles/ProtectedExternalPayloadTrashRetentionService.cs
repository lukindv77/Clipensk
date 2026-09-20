using System.Globalization;
using Clipensk.Core.Storage;

namespace Clipensk.Storage.ExternalFiles;

public sealed record ExternalPayloadTrashRetentionResult(
    int DeletedDateDirectoryCount,
    int DeletedFileCount,
    IReadOnlyList<string> SkippedEntryNames);

/// <summary>
/// Permanently deletes expired external payloads from Trash, per <c>docs/REQUIREMENTS.md</c> §15.
///
/// This is the only component in Clipensk that destroys user data with no copy left behind, so it
/// is deliberately narrow. It deletes nothing it cannot fully account for:
///
/// <list type="bullet">
/// <item>only entries directly under the configured Trash root are considered, and only directories
/// whose name is an exact <c>yyyy-MM-dd</c> deletion date. Anything else — a stray file, a
/// differently named directory — is reported as skipped and left untouched, because Clipensk did
/// not create it and cannot date it.</item>
/// <item>a deletion date in the future relative to the supplied local date is skipped rather than
/// deleted. That state means a clock moved, and guessing which direction would destroy data.</item>
/// <item>the whole subtree is validated before anything is removed. A reparse point anywhere in it
/// fails the operation closed, so a link planted inside Trash can never make this delete outside
/// it.</item>
/// </list>
///
/// The expiry rule is that a payload survives <c>retentionDays</c> complete days: a directory dated
/// <c>D</c> is deleted on <c>D + retentionDays</c> and not before.
///
/// It holds the storage mutation lease because
/// <see cref="ProtectedExternalPayloadTrashCollector"/> writes into the same directories.
/// </summary>
public sealed class ProtectedExternalPayloadTrashRetentionService
{
    private const string DeletionDateFormat = "yyyy-MM-dd";

    private readonly ProtectedStorageSessionLease _session;
    private readonly string _trashRootPath;
    private readonly string _trashRootPrefix;

    public ProtectedExternalPayloadTrashRetentionService(ProtectedStorageSessionLease session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        string dataRootPath = Path.GetFullPath(session.DataRootPath);
        _trashRootPath = Path.TrimEndingDirectorySeparator(Path.Combine(dataRootPath, "Trash"));
        _trashRootPrefix = _trashRootPath + Path.DirectorySeparatorChar;
    }

    public async Task<ExternalPayloadTrashRetentionResult> CollectAsync(
        DateOnly currentLocalDate,
        int retentionDays,
        CancellationToken cancellationToken = default)
    {
        if (retentionDays <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retentionDays),
                "Trash retention must keep expired payloads for at least one day.");
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            cancellationToken);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();

        using ProtectedStorageMutationLease mutationLease =
            await _session.AcquireMutationLeaseAsync(token).ConfigureAwait(false);

        return await Task.Run(
            () => CollectCore(currentLocalDate, retentionDays, token),
            CancellationToken.None).ConfigureAwait(false);
    }

    private ExternalPayloadTrashRetentionResult CollectCore(
        DateOnly currentLocalDate,
        int retentionDays,
        CancellationToken token)
    {
        var skipped = new List<string>();
        if (!Directory.Exists(_trashRootPath))
        {
            return new ExternalPayloadTrashRetentionResult(0, 0, skipped);
        }

        EnsureNotReparsePoint(
            _trashRootPath,
            "Configured Trash root is a reparse point during retention cleanup.");

        int deletedDirectories = 0;
        int deletedFiles = 0;

        foreach (string entry in Directory.EnumerateFileSystemEntries(_trashRootPath))
        {
            token.ThrowIfCancellationRequested();
            string name = Path.GetFileName(entry);

            if (!Directory.Exists(entry))
            {
                // Clipensk only ever creates deletion-date directories here.
                skipped.Add(name);
                continue;
            }

            if (!DateOnly.TryParseExact(
                    name,
                    DeletionDateFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateOnly deletionDate))
            {
                skipped.Add(name);
                continue;
            }

            if (deletionDate > currentLocalDate)
            {
                // A future deletion date means a clock moved; deleting on that basis is unsafe.
                skipped.Add(name);
                continue;
            }

            if (deletionDate.AddDays(retentionDays) > currentLocalDate)
            {
                continue;
            }

            deletedFiles += DeleteExpiredDirectory(entry, token);
            deletedDirectories++;
        }

        return new ExternalPayloadTrashRetentionResult(deletedDirectories, deletedFiles, skipped);
    }

    /// <summary>
    /// Validates the entire subtree before removing anything, so a rejected tree is left exactly as
    /// it was instead of half deleted.
    /// </summary>
    private int DeleteExpiredDirectory(string dateDirectory, CancellationToken token)
    {
        EnsureContained(dateDirectory);
        EnsureNotReparsePoint(
            dateDirectory,
            "Trash deletion-date directory is a reparse point during retention cleanup.");

        var directories = new List<string>();
        var files = new List<string>();
        CollectSubtree(dateDirectory, directories, files, token);

        foreach (string file in files)
        {
            token.ThrowIfCancellationRequested();
            File.Delete(file);
        }

        // Deepest first, so every directory is empty by the time it is removed.
        for (int index = directories.Count - 1; index >= 0; index--)
        {
            token.ThrowIfCancellationRequested();
            Directory.Delete(directories[index], recursive: false);
        }

        Directory.Delete(dateDirectory, recursive: false);
        return files.Count;
    }

    private void CollectSubtree(
        string directory,
        List<string> directories,
        List<string> files,
        CancellationToken token)
    {
        foreach (string child in Directory.EnumerateFileSystemEntries(directory))
        {
            token.ThrowIfCancellationRequested();
            EnsureContained(child);

            if (Directory.Exists(child))
            {
                EnsureNotReparsePoint(
                    child,
                    "Trash subdirectory is a reparse point during retention cleanup.");
                directories.Add(child);
                CollectSubtree(child, directories, files, token);
                continue;
            }

            EnsureNotReparsePoint(
                child,
                "Trash file is a reparse point during retention cleanup.");
            files.Add(child);
        }
    }

    private void EnsureContained(string path)
    {
        if (!Path.GetFullPath(path).StartsWith(_trashRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Trash retention cleanup refused a path outside the configured Trash root.");
        }
    }

    private static void EnsureNotReparsePoint(string path, string errorMessage)
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
                "Trash filesystem metadata could not be validated safely.",
                exception);
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException(errorMessage);
        }
    }
}
