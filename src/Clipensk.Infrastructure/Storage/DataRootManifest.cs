using Clipensk.Core.Settings;
using Clipensk.Infrastructure.Security;

namespace Clipensk.Infrastructure.Storage;

internal sealed record DataRootManifestFile(string RelativePath, long Length);

/// <summary>
/// The exact tree of a data root: every directory and file by relative path, files with their
/// lengths. Built without following links — a link anywhere in the tree refuses the relocation —
/// and including hidden and system entries, so a copy that matches the manifest is the whole tree.
/// </summary>
internal sealed class DataRootManifest
{
    private static readonly EnumerationOptions DirectChildren = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
    };

    private DataRootManifest(IReadOnlyList<string> directories, IReadOnlyList<DataRootManifestFile> files)
    {
        Directories = directories;
        Files = files;
        ByteCount = files.Sum(static file => file.Length);
    }

    /// <summary>Relative directory paths, parents before children.</summary>
    public IReadOnlyList<string> Directories { get; }

    public IReadOnlyList<DataRootManifestFile> Files { get; }

    public long ByteCount { get; }

    public static DataRootManifest Build(string root, CancellationToken cancellationToken)
    {
        var rootDirectory = new DirectoryInfo(root);
        if (!rootDirectory.Exists)
        {
            throw new DirectoryNotFoundException($"Data root '{root}' does not exist.");
        }

        var directories = new List<string>();
        var files = new List<DataRootManifestFile>();
        var names = new HashSet<string>(DataRootPaths.Comparer);
        var pending = new Stack<DirectoryInfo>();
        pending.Push(rootDirectory);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DirectoryInfo directory = pending.Pop();
            foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos("*", DirectChildren))
            {
                string relativePath = Path.GetRelativePath(rootDirectory.FullName, entry.FullName);
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new DataRootRelocationRefusedException(
                        DataRootRelocationRefusal.SourceContainsLink,
                        $"'{relativePath}' is a link; a data root with links is not relocated.");
                }
                if (!names.Add(relativePath))
                {
                    throw new DataRootRelocationRefusedException(
                        DataRootRelocationRefusal.NameCollision,
                        $"'{relativePath}' differs from another entry only by letter case.");
                }

                if (entry is DirectoryInfo child)
                {
                    directories.Add(relativePath);
                    pending.Push(child);
                }
                else if (entry is FileInfo file)
                {
                    files.Add(new DataRootManifestFile(relativePath, file.Length));
                }
            }
        }

        return new DataRootManifest(
            directories
                .OrderBy(Depth)
                .ThenBy(static path => path, StringComparer.Ordinal)
                .ToArray(),
            files
                .OrderBy(static file => file.RelativePath, StringComparer.Ordinal)
                .ToArray());
    }

    /// <summary>Same directories and same files with the same lengths, compared without case.</summary>
    public bool HasSameEntriesAs(DataRootManifest other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Directories.Count != other.Directories.Count || Files.Count != other.Files.Count)
        {
            return false;
        }

        var directories = new HashSet<string>(other.Directories, DataRootPaths.Comparer);
        if (!Directories.All(directories.Contains))
        {
            return false;
        }

        Dictionary<string, long> lengths = other.Files.ToDictionary(
            static file => file.RelativePath,
            static file => file.Length,
            DataRootPaths.Comparer);
        return Files.All(file => lengths.TryGetValue(file.RelativePath, out long length) && length == file.Length);
    }

    /// <summary>
    /// Files in the order the source is deleted: the crypto metadata last, so a partly removed
    /// source keeps saying what it was until nothing else of it is left.
    /// </summary>
    public IEnumerable<DataRootManifestFile> FilesInRemovalOrder() =>
        Files
            .OrderBy(static file => IsCryptoMetadata(file.RelativePath) ? 1 : 0)
            .ThenBy(static file => file.RelativePath, StringComparer.Ordinal);

    public IEnumerable<string> DirectoriesDeepestFirst() => Directories.Reverse();

    /// <summary>Everything still under <paramref name="root"/>, without descending into links.</summary>
    public static IReadOnlyList<string> ListRemainingEntries(string root)
    {
        var rootDirectory = new DirectoryInfo(root);
        var remaining = new List<string>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(rootDirectory);
        while (pending.Count > 0)
        {
            DirectoryInfo directory = pending.Pop();
            foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos("*", DirectChildren))
            {
                remaining.Add(Path.GetRelativePath(rootDirectory.FullName, entry.FullName));
                if (entry is DirectoryInfo child && !child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    pending.Push(child);
                }
            }
        }

        remaining.Sort(StringComparer.Ordinal);
        return remaining;
    }

    private static bool IsCryptoMetadata(string relativePath) =>
        DataRootPaths.Comparer.Equals(relativePath, FileProtectedStorageCredentialService.MetadataFileName);

    private static int Depth(string relativePath) =>
        relativePath.Count(static character =>
            character == Path.DirectorySeparatorChar || character == Path.AltDirectorySeparatorChar);
}

/// <summary>
/// Every source file opened exclusively for reading. While it is held nothing else — an operation
/// still finishing after the lock, an indexer, another process — can open or change a source file,
/// and acquiring it proves nothing had one open.
/// </summary>
internal sealed class DataRootSourceLease : IDisposable
{
    private readonly Dictionary<string, FileStream> _streams;

    private DataRootSourceLease(Dictionary<string, FileStream> streams)
    {
        _streams = streams;
    }

    public static DataRootSourceLease Acquire(
        string root,
        DataRootManifest manifest,
        CancellationToken cancellationToken)
    {
        var streams = new Dictionary<string, FileStream>(DataRootPaths.Comparer);
        try
        {
            foreach (DataRootManifestFile file in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileStream stream;
                try
                {
                    stream = new FileStream(
                        Path.Combine(root, file.RelativePath),
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.None,
                        bufferSize: 0,
                        FileOptions.SequentialScan);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new DataRootRelocationRefusedException(
                        DataRootRelocationRefusal.SourceBusy,
                        $"'{file.RelativePath}' is in use and cannot be relocated now.",
                        exception);
                }

                streams.Add(file.RelativePath, stream);
                if (stream.Length != file.Length)
                {
                    throw new DataRootRelocationRefusedException(
                        DataRootRelocationRefusal.SourceBusy,
                        $"'{file.RelativePath}' changed while the relocation was prepared.");
                }
            }

            // With every file held, the tree must still be the one that was listed.
            if (!DataRootManifest.Build(root, cancellationToken).HasSameEntriesAs(manifest))
            {
                throw new DataRootRelocationRefusedException(
                    DataRootRelocationRefusal.SourceBusy,
                    "The data root changed while the relocation was prepared.");
            }

            return new DataRootSourceLease(streams);
        }
        catch
        {
            foreach (FileStream stream in streams.Values)
            {
                stream.Dispose();
            }

            throw;
        }
    }

    public FileStream Get(string relativePath) => _streams[relativePath];

    public void Dispose()
    {
        foreach (FileStream stream in _streams.Values)
        {
            stream.Dispose();
        }

        _streams.Clear();
    }
}
