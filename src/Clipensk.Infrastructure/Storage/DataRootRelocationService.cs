using System.Security.Cryptography;
using Clipensk.Core.Settings;
using Clipensk.Core.Storage;

namespace Clipensk.Infrastructure.Storage;

/// <summary>
/// Why an operation on the data root as a whole — relocation, backup
/// (<see cref="DataRootBackupService"/>) or opening another one (<see cref="DataRootOpenService"/>) —
/// refused before writing anything.
/// </summary>
public enum DataRootRelocationRefusal
{
    RelocationPending,
    DataRootNotConfigured,
    SourceMissing,
    SourceNotStorage,
    SourceContainsLink,
    NameCollision,
    TargetSameAsSource,
    TargetNested,
    TargetIsFile,
    TargetIsLink,
    TargetNotEmpty,
    InsufficientSpace,
    SourceBusy,

    /// <summary>The chosen folder does not exist.</summary>
    TargetMissing,

    /// <summary>The chosen folder is an unfinished backup (<see cref="DataRootBackupService.IncompleteSuffix"/>).</summary>
    TargetIncompleteBackup,

    /// <summary>The chosen folder holds no Clipensk storage database.</summary>
    TargetNotStorage,
}

/// <summary>An operation on the data root refused before anything was written anywhere.</summary>
public sealed class DataRootRelocationRefusedException : InvalidOperationException
{
    public DataRootRelocationRefusedException(
        DataRootRelocationRefusal reason,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
    }

    public DataRootRelocationRefusal Reason { get; }
}

public sealed record DataRootRelocationPreview(
    string SourcePath,
    string TargetPath,
    int FileCount,
    long ByteCount,
    long RequiredBytes,
    long? AvailableBytes);

public sealed record DataRootRelocationProgress(int FilesCopied, int FileCount, long BytesCopied, long ByteCount);

/// <summary>
/// What removing the source left behind: <see cref="Retained"/> lists relative paths still in the
/// source; <see cref="RetryPending"/> tells whether some of them could not be deleted for a reason
/// that may pass (a busy file), so the marker stays and removal is retried at the next start.
/// Files whose content does not match the target copy, and files the relocation did not copy, are
/// retained without a retry.
/// </summary>
public sealed record DataRootSourceRemoval(IReadOnlyList<string> Retained, bool RetryPending)
{
    public static DataRootSourceRemoval Complete { get; } = new([], false);
}

public sealed record DataRootRelocationResult(
    string DataRootPath,
    int FileCount,
    long ByteCount,
    DataRootSourceRemoval SourceRemoval);

public enum DataRootRelocationRecoveryOutcome
{
    NothingPending,

    /// <summary>An unfinished copy was removed; the data root is still the source.</summary>
    RolledBack,

    /// <summary>A committed relocation finished removing its source.</summary>
    Completed,

    /// <summary>A committed relocation still has source files to delete at a later start.</summary>
    SourceRemovalPending,
}

public sealed record DataRootRelocationRecoveryResult(
    DataRootRelocationRecoveryOutcome Outcome,
    string? DataRootPath,
    IReadOnlyList<string> Retained);

/// <summary>
/// Moves the whole data root to a new directory, per <c>docs/DATA_ROOT_RELOCATION_PROTOCOL.md</c>:
/// byte copy of the source tree under exclusively held source handles, SHA-256 verification, one
/// atomic write of the new path as the commit point, then removal of the source; an interrupted
/// relocation is rolled back or finished by <see cref="RecoverAsync"/> according to its marker. It
/// runs while Clipensk is locked: no key is needed and nothing in the data root is decrypted.
/// </summary>
public sealed class DataRootRelocationService
{
    internal const string TemporarySuffix = DataRootTreeCopy.TemporarySuffix;

    private readonly IDataRootLocationStore _store;
    private readonly Func<string, long?> _availableFreeSpace;
    private readonly TimeProvider _time;

    public DataRootRelocationService(
        IDataRootLocationStore store,
        Func<string, long?>? availableFreeSpace = null,
        TimeProvider? time = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _availableFreeSpace = availableFreeSpace ?? DataRootTreeCopy.GetAvailableFreeSpace;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Checks the preconditions and sizes a relocation without changing anything.</summary>
    public async Task<DataRootRelocationPreview> InspectAsync(
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        DataRootLocationState state = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        (string source, string target) = RequirePaths(state, targetPath);
        DataRootManifest manifest = DataRootManifest.Build(source, cancellationToken);
        return RequireSpace(source, target, manifest);
    }

    /// <summary>
    /// Relocates the data root to <paramref name="targetPath"/>. <paramref name="protectTarget"/>
    /// runs after the marker is written and before anything is created in the target, so access
    /// rules are in place before the first byte lands there. Cancellation is honored only before the
    /// commit; afterwards the relocation always finishes.
    /// </summary>
    public async Task<DataRootRelocationResult> RelocateAsync(
        string targetPath,
        Action<string>? protectTarget = null,
        IProgress<DataRootRelocationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        DataRootLocationState state = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        (string source, string target) = RequirePaths(state, targetPath);
        bool targetExisted = Directory.Exists(target);
        DataRootManifest manifest = DataRootManifest.Build(source, cancellationToken);
        RequireSpace(source, target, manifest);

        using DataRootSourceLease lease = DataRootSourceLease.Acquire(source, manifest, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var marker = new DataRootRelocationMarker(
            Guid.NewGuid(),
            source,
            target,
            targetExisted,
            DataRootRelocationPhase.Copying,
            _time.GetUtcNow().ToUniversalTime());
        await _store.WriteAsync(new DataRootLocationState(source, marker), cancellationToken)
            .ConfigureAwait(false);

        Dictionary<string, byte[]> hashes;
        try
        {
            protectTarget?.Invoke(target);
            Directory.CreateDirectory(target);
            hashes = DataRootTreeCopy.CopyTree(lease, manifest, target, progress, cancellationToken);
            DataRootTreeCopy.VerifyTree(manifest, target, hashes, cancellationToken);

            // Last cancellation boundary: once the new path is written the relocation completes.
            cancellationToken.ThrowIfCancellationRequested();
            await _store.WriteAsync(
                    new DataRootLocationState(target, marker with { Phase = DataRootRelocationPhase.Switched }),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            lease.Dispose();
            await TryRollBackAfterFailureAsync(marker).ConfigureAwait(false);
            throw;
        }

        lease.Dispose();
        DataRootSourceRemoval removal = RemoveSource(source, target, manifest, hashes);
        if (!removal.RetryPending)
        {
            await _store.WriteAsync(new DataRootLocationState(target, null), CancellationToken.None)
                .ConfigureAwait(false);
        }

        return new DataRootRelocationResult(target, manifest.Files.Count, manifest.ByteCount, removal);
    }

    /// <summary>
    /// Finishes or undoes an interrupted relocation at startup, before anything opens the data
    /// root. A marker the protocol cannot resolve unambiguously fails closed and changes nothing.
    /// </summary>
    public async Task<DataRootRelocationRecoveryResult> RecoverAsync(CancellationToken cancellationToken = default)
    {
        DataRootLocationState state = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (state.PendingRelocation is not { } marker)
        {
            return new DataRootRelocationRecoveryResult(
                DataRootRelocationRecoveryOutcome.NothingPending,
                state.DataRootPath,
                []);
        }

        marker.Validate();
        switch (marker.Phase)
        {
            case DataRootRelocationPhase.Copying:
            {
                RequireDataRoot(state, marker.SourcePath);
                IReadOnlyList<string> retained = RollBackCopy(marker, cancellationToken);
                await _store.WriteAsync(new DataRootLocationState(marker.SourcePath, null), cancellationToken)
                    .ConfigureAwait(false);
                return new DataRootRelocationRecoveryResult(
                    DataRootRelocationRecoveryOutcome.RolledBack,
                    marker.SourcePath,
                    retained);
            }
            case DataRootRelocationPhase.Switched:
            {
                RequireDataRoot(state, marker.TargetPath);
                DataRootManifest targetManifest = DataRootManifest.Build(marker.TargetPath, cancellationToken);
                DataRootSourceRemoval removal = RemoveSource(
                    marker.SourcePath,
                    marker.TargetPath,
                    targetManifest,
                    knownTargetHashes: null);
                if (removal.RetryPending)
                {
                    return new DataRootRelocationRecoveryResult(
                        DataRootRelocationRecoveryOutcome.SourceRemovalPending,
                        marker.TargetPath,
                        removal.Retained);
                }

                await _store.WriteAsync(new DataRootLocationState(marker.TargetPath, null), cancellationToken)
                    .ConfigureAwait(false);
                return new DataRootRelocationRecoveryResult(
                    DataRootRelocationRecoveryOutcome.Completed,
                    marker.TargetPath,
                    removal.Retained);
            }
            default:
                throw new InvalidDataException($"Unknown data root relocation phase '{(int)marker.Phase}'.");
        }
    }

    private static (string Source, string Target) RequirePaths(DataRootLocationState state, string targetPath)
    {
        if (state.PendingRelocation is not null)
        {
            throw Refused(DataRootRelocationRefusal.RelocationPending, "A data root relocation is still unfinished.");
        }
        if (string.IsNullOrWhiteSpace(state.DataRootPath))
        {
            throw Refused(DataRootRelocationRefusal.DataRootNotConfigured, "No data root is configured.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        string source = DataRootPaths.Normalize(state.DataRootPath);
        string target = DataRootPaths.Normalize(targetPath);

        var sourceDirectory = new DirectoryInfo(source);
        if (!sourceDirectory.Exists)
        {
            throw Refused(DataRootRelocationRefusal.SourceMissing, "The current data root does not exist.");
        }
        if (sourceDirectory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw Refused(DataRootRelocationRefusal.SourceContainsLink, "The current data root is a link.");
        }
        if (StorageDatabaseFiles.EnumerateExisting(source).Count == 0)
        {
            throw Refused(DataRootRelocationRefusal.SourceNotStorage, "The current data root holds no Clipensk storage.");
        }

        if (DataRootPaths.AreSame(source, target))
        {
            throw Refused(DataRootRelocationRefusal.TargetSameAsSource, "The new location is the current one.");
        }
        if (DataRootPaths.IsNested(source, target) || DataRootPaths.IsNested(target, source))
        {
            throw Refused(DataRootRelocationRefusal.TargetNested, "The new location must not contain or lie inside the current one.");
        }
        if (File.Exists(target))
        {
            throw Refused(DataRootRelocationRefusal.TargetIsFile, "The new location is a file.");
        }

        var targetDirectory = new DirectoryInfo(target);
        if (targetDirectory.Exists)
        {
            if (targetDirectory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw Refused(DataRootRelocationRefusal.TargetIsLink, "The new location is a link.");
            }
            if (targetDirectory.EnumerateFileSystemInfos().Any())
            {
                throw Refused(DataRootRelocationRefusal.TargetNotEmpty, "The new location must be empty.");
            }
        }

        return (source, target);
    }

    private DataRootRelocationPreview RequireSpace(string source, string target, DataRootManifest manifest)
    {
        long required = DataRootTreeCopy.RequiredBytes(manifest);
        long? available = _availableFreeSpace(target);
        if (available is long bytes && bytes < required)
        {
            throw Refused(
                DataRootRelocationRefusal.InsufficientSpace,
                $"The new location has {bytes} free bytes; {required} are required.");
        }

        return new DataRootRelocationPreview(
            source,
            target,
            manifest.Files.Count,
            manifest.ByteCount,
            required,
            available);
    }

    /// <summary>
    /// Deletes source files that the target holds byte for byte, then the directories the
    /// relocation knows about once empty, and the source root once empty. Nothing the target does
    /// not hold identically is ever deleted.
    /// </summary>
    private static DataRootSourceRemoval RemoveSource(
        string source,
        string target,
        DataRootManifest manifest,
        IReadOnlyDictionary<string, byte[]>? knownTargetHashes)
    {
        var sourceDirectory = new DirectoryInfo(source);
        if (!sourceDirectory.Exists)
        {
            return DataRootSourceRemoval.Complete;
        }
        if (sourceDirectory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return new DataRootSourceRemoval(["."], RetryPending: false);
        }

        var retained = new List<string>();
        bool retryPending = false;
        foreach (DataRootManifestFile file in manifest.FilesInRemovalOrder())
        {
            string sourcePath = Path.Combine(source, file.RelativePath);
            var sourceFile = new FileInfo(sourcePath);
            if (!sourceFile.Exists)
            {
                continue;
            }
            if (sourceFile.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                !IsIdenticalToTarget(sourcePath, Path.Combine(target, file.RelativePath), file, knownTargetHashes))
            {
                retained.Add(file.RelativePath);
                continue;
            }

            try
            {
                if (sourceFile.Attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    sourceFile.Attributes &= ~FileAttributes.ReadOnly;
                }

                sourceFile.Delete();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                retained.Add(file.RelativePath);
                retryPending = true;
            }
        }

        foreach (string directory in manifest.DirectoriesDeepestFirst())
        {
            DataRootTreeCopy.TryDeleteEmptyDirectory(Path.Combine(source, directory));
        }

        DataRootTreeCopy.TryDeleteEmptyDirectory(source);
        if (Directory.Exists(source))
        {
            foreach (string remaining in DataRootManifest.ListRemainingEntries(source))
            {
                if (!retained.Contains(remaining, DataRootPaths.Comparer))
                {
                    retained.Add(remaining);
                }
            }
        }

        return new DataRootSourceRemoval(retained, retryPending);
    }

    private static bool IsIdenticalToTarget(
        string sourcePath,
        string targetPath,
        DataRootManifestFile file,
        IReadOnlyDictionary<string, byte[]>? knownTargetHashes)
    {
        try
        {
            byte[] expected;
            if (knownTargetHashes is not null)
            {
                expected = knownTargetHashes[file.RelativePath];
            }
            else
            {
                var targetFile = new FileInfo(targetPath);
                if (!targetFile.Exists || targetFile.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return false;
                }

                (long targetLength, byte[] targetDigest) = DataRootTreeCopy.HashFile(targetPath, CancellationToken.None);
                if (targetLength != file.Length)
                {
                    return false;
                }

                expected = targetDigest;
            }

            (long length, byte[] digest) = DataRootTreeCopy.HashFile(sourcePath, CancellationToken.None);
            return length == file.Length && CryptographicOperations.FixedTimeEquals(digest, expected);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Removes from the target what an unfinished copy could have created there: the manifest's
    /// files and temporary files, then the manifest's directories once empty, and the target root
    /// if the relocation created it and it is empty. Returns what had to stay.
    /// </summary>
    private static IReadOnlyList<string> RollBackCopy(DataRootRelocationMarker marker, CancellationToken cancellationToken)
    {
        var targetDirectory = new DirectoryInfo(marker.TargetPath);
        if (!targetDirectory.Exists)
        {
            return [];
        }
        if (targetDirectory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return ["."];
        }

        DataRootManifest sourceManifest = DataRootManifest.Build(marker.SourcePath, cancellationToken);
        DataRootTreeCopy.RemoveCopy(marker.TargetPath, sourceManifest);
        if (!marker.TargetExisted)
        {
            DataRootTreeCopy.TryDeleteEmptyDirectory(marker.TargetPath);
        }

        return Directory.Exists(marker.TargetPath)
            ? DataRootManifest.ListRemainingEntries(marker.TargetPath)
            : [];
    }

    private async Task TryRollBackAfterFailureAsync(DataRootRelocationMarker marker)
    {
        try
        {
            // A failed commit write replaces nothing (the store writes atomically); if it did land,
            // the relocation is committed and the next start finishes it instead.
            DataRootLocationState state = await _store.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            if (state.PendingRelocation is not { Phase: DataRootRelocationPhase.Copying } pending ||
                pending.OperationId != marker.OperationId)
            {
                return;
            }

            RollBackCopy(marker, CancellationToken.None);
            await _store.WriteAsync(new DataRootLocationState(marker.SourcePath, null), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // The Copying marker stays; startup recovery rolls the copy back.
        }
    }

    private static void RequireDataRoot(DataRootLocationState state, string expected)
    {
        if (string.IsNullOrWhiteSpace(state.DataRootPath) ||
            !DataRootPaths.AreSame(state.DataRootPath, expected))
        {
            throw new InvalidDataException(
                "The configured data root does not match the pending relocation; nothing was changed.");
        }
    }

    private static DataRootRelocationRefusedException Refused(DataRootRelocationRefusal reason, string message) =>
        new(reason, message);
}
