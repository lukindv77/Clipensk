using Clipensk.Core.Settings;
using Clipensk.Core.Storage;

namespace Clipensk.Infrastructure.Storage;

public sealed record DataRootOpenPreview(string? CurrentPath, string NewPath, int DatabaseCount);

/// <summary>
/// Makes an existing folder with a Clipensk storage — a backup, for instance — the data root, per
/// <c>docs/BACKUP_PROTOCOL.md</c> §4: one atomic write of the data root path. Nothing is copied,
/// moved or deleted; the previous data root stays where it is.
/// </summary>
public sealed class DataRootOpenService
{
    private readonly IDataRootLocationStore _store;

    public DataRootOpenService(IDataRootLocationStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>Checks that <paramref name="folder"/> can become the data root, without changing anything.</summary>
    public async Task<DataRootOpenPreview> InspectAsync(string folder, CancellationToken cancellationToken = default)
    {
        DataRootLocationState state = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        return Require(state, folder);
    }

    /// <summary>Checks again and makes <paramref name="folder"/> the data root. Returns its normalized path.</summary>
    public async Task<string> OpenAsync(string folder, CancellationToken cancellationToken = default)
    {
        DataRootLocationState state = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        DataRootOpenPreview preview = Require(state, folder);
        await _store.WriteAsync(new DataRootLocationState(preview.NewPath, null), cancellationToken)
            .ConfigureAwait(false);
        return preview.NewPath;
    }

    private static DataRootOpenPreview Require(DataRootLocationState state, string folder)
    {
        if (state.PendingRelocation is not null)
        {
            throw Refused(DataRootRelocationRefusal.RelocationPending, "A data root relocation is still unfinished.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        string path = DataRootPaths.Normalize(folder);
        if (File.Exists(path))
        {
            throw Refused(DataRootRelocationRefusal.TargetIsFile, "The chosen location is a file.");
        }

        var directory = new DirectoryInfo(path);
        if (!directory.Exists)
        {
            throw Refused(DataRootRelocationRefusal.TargetMissing, "The chosen folder does not exist.");
        }
        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw Refused(DataRootRelocationRefusal.TargetIsLink, "The chosen folder is a link.");
        }
        if (directory.Name.EndsWith(DataRootBackupService.IncompleteSuffix, StringComparison.OrdinalIgnoreCase))
        {
            throw Refused(DataRootRelocationRefusal.TargetIncompleteBackup, "The chosen folder is an unfinished backup.");
        }

        string? current = string.IsNullOrWhiteSpace(state.DataRootPath)
            ? null
            : DataRootPaths.Normalize(state.DataRootPath);
        if (current is not null)
        {
            if (DataRootPaths.AreSame(current, path))
            {
                throw Refused(DataRootRelocationRefusal.TargetSameAsSource, "The chosen folder is the current data root.");
            }
            if (DataRootPaths.IsNested(current, path) || DataRootPaths.IsNested(path, current))
            {
                throw Refused(DataRootRelocationRefusal.TargetNested, "The chosen folder must not contain or lie inside the current data root.");
            }
        }

        int databases = StorageDatabaseFiles.EnumerateExisting(path).Count;
        if (databases == 0)
        {
            throw Refused(DataRootRelocationRefusal.TargetNotStorage, "The chosen folder holds no Clipensk storage.");
        }

        return new DataRootOpenPreview(current, path, databases);
    }

    private static DataRootRelocationRefusedException Refused(DataRootRelocationRefusal reason, string message) =>
        new(reason, message);
}
