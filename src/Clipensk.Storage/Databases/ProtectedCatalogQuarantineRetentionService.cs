using Clipensk.Core.Storage;

namespace Clipensk.Storage.Databases;

public sealed record CatalogQuarantineRetentionResult(
    int DeletedFileCount,
    IReadOnlyList<string> SkippedEntryNames);

/// <summary>
/// Deletes the replaced Catalogs kept in <c>Current/CatalogQuarantine</c> once they are as old as
/// the Trash retention (user decision 2026-09-23, <c>docs/OPEN_QUESTIONS.md</c> §4). A Catalog is
/// only a projection of Current and Archive, so a quarantined copy is a diagnostic aid, not data.
///
/// The rule is the one Trash uses: a copy quarantined on UTC date <c>D</c> is deleted on
/// <c>D + retentionDays</c> and not before. Only files named by
/// <see cref="CatalogQuarantineFileName"/> directly in the directory are considered; anything else,
/// a copy dated in the future (a clock moved) and a link are left untouched and reported.
/// </summary>
public sealed class ProtectedCatalogQuarantineRetentionService
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly string _quarantinePath;

    public ProtectedCatalogQuarantineRetentionService(ProtectedStorageSessionLease session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _quarantinePath = Path.Combine(
            Path.GetFullPath(session.DataRootPath),
            StorageDatabaseFiles.CurrentDirectoryName,
            CatalogQuarantineFileName.DirectoryName);
    }

    public async Task<CatalogQuarantineRetentionResult> CollectAsync(
        DateOnly currentLocalDate,
        int retentionDays,
        CancellationToken cancellationToken = default)
    {
        if (retentionDays <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retentionDays),
                "Catalog quarantine retention must keep a copy for at least one day.");
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

    private CatalogQuarantineRetentionResult CollectCore(
        DateOnly currentLocalDate,
        int retentionDays,
        CancellationToken token)
    {
        var skipped = new List<string>();
        var directory = new DirectoryInfo(_quarantinePath);
        if (!directory.Exists)
        {
            return new CatalogQuarantineRetentionResult(0, skipped);
        }

        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("Catalog quarantine directory is a reparse point during retention cleanup.");
        }

        int deleted = 0;
        foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
        {
            token.ThrowIfCancellationRequested();
            if (entry is not FileInfo file ||
                file.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                !CatalogQuarantineFileName.TryParse(file.Name, out DateTimeOffset quarantinedAtUtc))
            {
                skipped.Add(entry.Name);
                continue;
            }

            DateOnly quarantineDate = DateOnly.FromDateTime(quarantinedAtUtc.UtcDateTime);
            if (quarantineDate > currentLocalDate)
            {
                // A copy dated in the future means a clock moved; deleting on that basis is unsafe.
                skipped.Add(entry.Name);
                continue;
            }

            if (quarantineDate.AddDays(retentionDays) > currentLocalDate)
            {
                continue;
            }

            file.Delete();
            deleted++;
        }

        return new CatalogQuarantineRetentionResult(deleted, skipped);
    }
}
