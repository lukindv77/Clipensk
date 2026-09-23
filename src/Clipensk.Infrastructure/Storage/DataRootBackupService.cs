using System.Globalization;
using Clipensk.Core.Settings;
using Clipensk.Core.Storage;

namespace Clipensk.Infrastructure.Storage;

public sealed record DataRootBackupPreview(
    string SourcePath,
    string BackupPath,
    int FileCount,
    long ByteCount,
    long RequiredBytes,
    long? AvailableBytes);

public sealed record DataRootBackupResult(string BackupPath, int FileCount, long ByteCount);

/// <summary>
/// A backup of the whole data root, per <c>docs/BACKUP_PROTOCOL.md</c> §3: a byte copy of the tree
/// under exclusively held source handles into a new <c>Clipensk-backup-…</c> folder, verified by
/// SHA-256. The copy is written under the <see cref="IncompleteSuffix"/> name and takes its final
/// name only once verified, so a folder without the suffix is always a whole backup. The data root
/// and the settings are never changed. It runs while Clipensk is locked; no key is needed.
/// </summary>
public sealed class DataRootBackupService
{
    public const string FolderPrefix = "Clipensk-backup-";
    public const string IncompleteSuffix = ".incomplete";

    private readonly IDataRootLocationStore _store;
    private readonly Func<string, long?> _availableFreeSpace;
    private readonly TimeProvider _time;

    public DataRootBackupService(
        IDataRootLocationStore store,
        Func<string, long?>? availableFreeSpace = null,
        TimeProvider? time = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _availableFreeSpace = availableFreeSpace ?? DataRootTreeCopy.GetAvailableFreeSpace;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Names the backup folder inside <paramref name="parentFolder"/> after the local time, checks
    /// the preconditions and sizes the copy without changing anything.
    /// </summary>
    public async Task<DataRootBackupPreview> InspectAsync(
        string parentFolder,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentFolder);
        string backupPath = Path.Combine(
            DataRootPaths.Normalize(parentFolder),
            FolderPrefix + _time.GetLocalNow().ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture));

        DataRootLocationState state = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        (string source, string backup) = RequirePaths(state, backupPath);
        DataRootManifest manifest = DataRootManifest.Build(source, cancellationToken);
        return RequireSpace(source, backup, manifest);
    }

    /// <summary>
    /// Copies the data root into <paramref name="backupPath"/> (as <see cref="InspectAsync"/> named
    /// it). <paramref name="protectTarget"/> runs on the unfinished folder before its first byte.
    /// Cancellation is honored until the folder takes its final name.
    /// </summary>
    public async Task<DataRootBackupResult> CreateAsync(
        string backupPath,
        Action<string>? protectTarget = null,
        IProgress<DataRootRelocationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        DataRootLocationState state = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        (string source, string backup) = RequirePaths(state, backupPath);
        DataRootManifest manifest = DataRootManifest.Build(source, cancellationToken);
        RequireSpace(source, backup, manifest);

        using DataRootSourceLease lease = DataRootSourceLease.Acquire(source, manifest, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        string incomplete = backup + IncompleteSuffix;
        try
        {
            protectTarget?.Invoke(incomplete);
            Directory.CreateDirectory(incomplete);
            Dictionary<string, byte[]> hashes =
                DataRootTreeCopy.CopyTree(lease, manifest, incomplete, progress, cancellationToken);
            DataRootTreeCopy.VerifyTree(manifest, incomplete, hashes, cancellationToken);

            // Last cancellation boundary: once renamed, the folder is a finished backup.
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(incomplete, backup);
        }
        catch
        {
            lease.Dispose();
            TryRemoveIncomplete(incomplete, manifest);
            throw;
        }

        return new DataRootBackupResult(backup, manifest.Files.Count, manifest.ByteCount);
    }

    private static (string Source, string Backup) RequirePaths(DataRootLocationState state, string backupPath)
    {
        if (state.PendingRelocation is not null)
        {
            throw Refused(DataRootRelocationRefusal.RelocationPending, "A data root relocation is still unfinished.");
        }
        if (string.IsNullOrWhiteSpace(state.DataRootPath))
        {
            throw Refused(DataRootRelocationRefusal.DataRootNotConfigured, "No data root is configured.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);

        string source = DataRootPaths.Normalize(state.DataRootPath);
        var sourceDirectory = new DirectoryInfo(source);
        if (!sourceDirectory.Exists)
        {
            throw Refused(DataRootRelocationRefusal.SourceMissing, "The data root does not exist.");
        }
        if (sourceDirectory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw Refused(DataRootRelocationRefusal.SourceContainsLink, "The data root is a link.");
        }
        if (StorageDatabaseFiles.EnumerateExisting(source).Count == 0)
        {
            throw Refused(DataRootRelocationRefusal.SourceNotStorage, "The data root holds no Clipensk storage.");
        }

        string backup = DataRootPaths.Normalize(backupPath);
        string? parent = Path.GetDirectoryName(backup);
        if (parent is null || File.Exists(parent))
        {
            throw Refused(DataRootRelocationRefusal.TargetIsFile, "The chosen location is not a folder.");
        }

        var parentDirectory = new DirectoryInfo(parent);
        if (!parentDirectory.Exists)
        {
            throw Refused(DataRootRelocationRefusal.TargetMissing, "The chosen folder does not exist.");
        }
        if (parentDirectory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw Refused(DataRootRelocationRefusal.TargetIsLink, "The chosen folder is a link.");
        }

        // A backup inside the data root would be copied into the next backup.
        if (DataRootPaths.AreSame(source, parent) || DataRootPaths.IsNested(source, parent))
        {
            throw Refused(DataRootRelocationRefusal.TargetNested, "A backup must not be kept inside the data root.");
        }

        string incomplete = backup + IncompleteSuffix;
        if (Directory.Exists(backup) || File.Exists(backup) ||
            Directory.Exists(incomplete) || File.Exists(incomplete))
        {
            throw Refused(DataRootRelocationRefusal.TargetNotEmpty, "The backup folder already exists.");
        }

        return (source, backup);
    }

    private DataRootBackupPreview RequireSpace(string source, string backup, DataRootManifest manifest)
    {
        long required = DataRootTreeCopy.RequiredBytes(manifest);
        long? available = _availableFreeSpace(backup);
        if (available is long bytes && bytes < required)
        {
            throw Refused(
                DataRootRelocationRefusal.InsufficientSpace,
                $"The chosen folder has {bytes} free bytes; {required} are required.");
        }

        return new DataRootBackupPreview(source, backup, manifest.Files.Count, manifest.ByteCount, required, available);
    }

    private static void TryRemoveIncomplete(string incomplete, DataRootManifest manifest)
    {
        try
        {
            var directory = new DirectoryInfo(incomplete);
            if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return;
            }

            DataRootTreeCopy.RemoveCopy(incomplete, manifest);
            DataRootTreeCopy.TryDeleteEmptyDirectory(incomplete);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The folder keeps its unfinished name; the caller reports that it is still there.
        }
    }

    private static DataRootRelocationRefusedException Refused(DataRootRelocationRefusal reason, string message) =>
        new(reason, message);
}
