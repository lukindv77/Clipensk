using System.Security.Cryptography;
using Clipensk.Core.Settings;

namespace Clipensk.Infrastructure.Storage;

/// <summary>
/// Copying a data root tree byte for byte from exclusively held source handles, verifying the copy
/// by SHA-256 and removing an unfinished copy — shared by relocation
/// (<c>docs/DATA_ROOT_RELOCATION_PROTOCOL.md</c>) and backup (<c>docs/BACKUP_PROTOCOL.md</c>).
/// </summary>
internal static class DataRootTreeCopy
{
    internal const string TemporarySuffix = ".clipensk-relocating";
    private const int BufferSize = 1 << 20;
    private const long MinimumSpareBytes = 64L * 1024 * 1024;

    /// <summary>The free space a copy of <paramref name="manifest"/> needs: its size plus 5 %, at least 64 MB.</summary>
    public static long RequiredBytes(DataRootManifest manifest) =>
        manifest.ByteCount + Math.Max(manifest.ByteCount / 20, MinimumSpareBytes);

    public static Dictionary<string, byte[]> CopyTree(
        DataRootSourceLease lease,
        DataRootManifest manifest,
        string target,
        IProgress<DataRootRelocationProgress>? progress,
        CancellationToken cancellationToken)
    {
        foreach (string directory in manifest.Directories)
        {
            Directory.CreateDirectory(Path.Combine(target, directory));
        }

        var hashes = new Dictionary<string, byte[]>(DataRootPaths.Comparer);
        byte[] buffer = new byte[BufferSize];
        long bytesCopied = 0;
        int filesCopied = 0;
        progress?.Report(new DataRootRelocationProgress(0, manifest.Files.Count, 0, manifest.ByteCount));
        foreach (DataRootManifestFile file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string finalPath = Path.Combine(target, file.RelativePath);
            string temporaryPath = finalPath + TemporarySuffix;
            if (File.Exists(finalPath) || Directory.Exists(finalPath) ||
                File.Exists(temporaryPath) || Directory.Exists(temporaryPath))
            {
                throw new IOException($"The target already holds '{file.RelativePath}'.");
            }

            FileStream source = lease.Get(file.RelativePath);
            source.Position = 0;
            using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                using (var destination = new FileStream(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           BufferSize,
                           FileOptions.SequentialScan))
                {
                    long copied = 0;
                    int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        hash.AppendData(buffer, 0, read);
                        destination.Write(buffer, 0, read);
                        copied += read;
                        bytesCopied += read;
                    }

                    if (copied != file.Length)
                    {
                        throw new IOException($"'{file.RelativePath}' changed size while it was copied.");
                    }

                    destination.Flush(flushToDisk: true);
                }

                hashes.Add(file.RelativePath, hash.GetHashAndReset());
            }

            File.Move(temporaryPath, finalPath, overwrite: false);
            filesCopied++;
            progress?.Report(new DataRootRelocationProgress(
                filesCopied,
                manifest.Files.Count,
                bytesCopied,
                manifest.ByteCount));
        }

        return hashes;
    }

    /// <summary>
    /// Rereads every copied file from the target and compares it with the source digest, then
    /// requires the target tree to hold exactly the manifest: no extra entry, no link, no temporary
    /// file.
    /// </summary>
    public static void VerifyTree(
        DataRootManifest manifest,
        string target,
        IReadOnlyDictionary<string, byte[]> hashes,
        CancellationToken cancellationToken)
    {
        foreach (DataRootManifestFile file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = Path.Combine(target, file.RelativePath);
            (long length, byte[] digest) = HashFile(path, cancellationToken);
            if (length != file.Length || !CryptographicOperations.FixedTimeEquals(digest, hashes[file.RelativePath]))
            {
                throw new InvalidDataException($"The copy of '{file.RelativePath}' does not match the source.");
            }
        }

        DataRootManifest copied = DataRootManifest.Build(target, cancellationToken);
        if (!copied.HasSameEntriesAs(manifest))
        {
            throw new InvalidDataException("The new location does not hold exactly the copied data root.");
        }
    }

    /// <summary>
    /// Removes what an unfinished copy of <paramref name="manifest"/> could have created under
    /// <paramref name="target"/>: its files and temporary files, then its directories once empty.
    /// The target root itself is left to the caller.
    /// </summary>
    public static void RemoveCopy(string target, DataRootManifest manifest)
    {
        foreach (DataRootManifestFile file in manifest.Files)
        {
            string finalPath = Path.Combine(target, file.RelativePath);
            DeleteRegularFile(finalPath);
            DeleteRegularFile(finalPath + TemporarySuffix);
        }

        foreach (string directory in manifest.DirectoriesDeepestFirst())
        {
            TryDeleteEmptyDirectory(Path.Combine(target, directory));
        }
    }

    public static void DeleteRegularFile(string path)
    {
        var file = new FileInfo(path);
        if (file.Exists && !file.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            file.Delete();
        }
    }

    public static void TryDeleteEmptyDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        if (!directory.Exists ||
            directory.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            directory.EnumerateFileSystemInfos().Any())
        {
            return;
        }

        try
        {
            directory.Delete(recursive: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static (long Length, byte[] Digest) HashFile(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.SequentialScan);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[BufferSize];
        long length = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
            length += read;
        }

        return (length, hash.GetHashAndReset());
    }

    public static long? GetAvailableFreeSpace(string target)
    {
        try
        {
            string? existing = target;
            while (existing is not null && !Directory.Exists(existing))
            {
                existing = Path.GetDirectoryName(existing);
            }

            return existing is null ? null : new DriveInfo(existing).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

}
