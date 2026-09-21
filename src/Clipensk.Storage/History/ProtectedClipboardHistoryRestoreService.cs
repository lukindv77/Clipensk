using System.Security.Cryptography;
using Clipensk.Core.History;
using Clipensk.Core.Storage;

namespace Clipensk.Storage.History;

/// <summary>
/// One payload of a history entry, resolved to something the Windows clipboard layer can publish.
/// Inline payloads carry their canonical text; external payloads carry a verified absolute path
/// together with the durable reference they were checked against.
/// </summary>
public sealed record RestorableClipboardPayload(
    int PayloadOrder,
    string FormatName,
    ClipboardHistoryPayloadKind Kind,
    string? InlineCanonicalText,
    string? ExternalFilePath,
    ClipboardHistoryExternalReference? ExternalReference,
    long CanonicalByteCount,
    string? SearchText);

public sealed record RestorableClipboardEntry(
    Guid EventId,
    IReadOnlyList<RestorableClipboardPayload> Payloads);

/// <summary>
/// Prepares one stored history entry for republication to the Windows clipboard.
///
/// The entry itself is supplied by the caller — the journal already holds it — so this service adds
/// no second history read path. What it does add is the external-payload **read** side, which had
/// no implementation: the store could only write. Every external payload is re-verified against the
/// filesystem at restore time rather than trusted from the database row, because external files are
/// physically collected into Trash when the last reference disappears, and restoring a stale or
/// substituted file would publish the wrong bytes to the clipboard.
///
/// Verification is deliberately the same contract
/// <see cref="ExternalFiles.ProtectedExternalPayloadTrashCollector"/> enforces when it moves those
/// files: the resolved path must stay inside the configured <c>Files</c> root, neither the root nor
/// the parent directory nor the file may be a reparse point, and both the size and the SHA-256 must
/// match the durable reference exactly. Anything else fails closed.
///
/// This service reads no database and holds no mutation lease: an external payload object is
/// content-addressed and immutable once written, so the only durable state it touches is the file
/// it verifies.
/// </summary>
public sealed class ProtectedClipboardHistoryRestoreService
{
    private readonly string _filesRootPath;
    private readonly string _filesRootPrefix;

    public ProtectedClipboardHistoryRestoreService(ProtectedStorageSessionLease session)
    {
        ArgumentNullException.ThrowIfNull(session);
        string dataRootPath = Path.GetFullPath(session.DataRootPath);
        _filesRootPath = Path.TrimEndingDirectorySeparator(Path.Combine(dataRootPath, "Files"));
        _filesRootPrefix = _filesRootPath + Path.DirectorySeparatorChar;
    }

    public async Task<RestorableClipboardEntry> PrepareAsync(
        ClipboardHistoryEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Payloads.Count == 0)
        {
            throw new InvalidDataException(
                "A history entry without payloads cannot be restored to the clipboard.");
        }

        List<ClipboardHistoryPayload> ordered = [.. entry.Payloads.OrderBy(item => item.PayloadOrder)];
        if (ordered.Select(item => item.PayloadOrder).Distinct().Count() != ordered.Count)
        {
            throw new InvalidDataException(
                "A history entry cannot contain duplicate payload orders.");
        }

        var prepared = new List<RestorableClipboardPayload>(ordered.Count);
        foreach (ClipboardHistoryPayload payload in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            prepared.Add(await PreparePayloadAsync(payload, cancellationToken).ConfigureAwait(false));
        }

        return new RestorableClipboardEntry(entry.EventId, prepared);
    }

    private async Task<RestorableClipboardPayload> PreparePayloadAsync(
        ClipboardHistoryPayload payload,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload.FormatName))
        {
            throw new InvalidDataException("A restorable payload requires a format name.");
        }

        switch (payload.Kind)
        {
            case ClipboardHistoryPayloadKind.Text:
            case ClipboardHistoryPayloadKind.Link:
            case ClipboardHistoryPayloadKind.StorageItems:
                if (payload.InlineCanonicalText is not { Length: > 0 } inlineText)
                {
                    throw new InvalidDataException(
                        $"Payload '{payload.FormatName}' is stored inline but carries no canonical text.");
                }

                if (payload.ExternalReference is not null)
                {
                    throw new InvalidDataException(
                        $"Payload '{payload.FormatName}' is stored inline and must not carry an external reference.");
                }

                return new RestorableClipboardPayload(
                    payload.PayloadOrder,
                    payload.FormatName,
                    payload.Kind,
                    inlineText,
                    ExternalFilePath: null,
                    ExternalReference: null,
                    payload.CanonicalByteCount,
                    payload.SearchText);

            case ClipboardHistoryPayloadKind.PngImage:
            case ClipboardHistoryPayloadKind.CustomBinary:
                if (payload.ExternalReference is not { } reference)
                {
                    throw new InvalidDataException(
                        $"Payload '{payload.FormatName}' is stored externally but carries no reference.");
                }

                if (payload.InlineCanonicalText is not null)
                {
                    throw new InvalidDataException(
                        $"Payload '{payload.FormatName}' is stored externally and must not carry inline text.");
                }

                string path = await ResolveVerifiedExternalPathAsync(reference, cancellationToken)
                    .ConfigureAwait(false);
                return new RestorableClipboardPayload(
                    payload.PayloadOrder,
                    payload.FormatName,
                    payload.Kind,
                    InlineCanonicalText: null,
                    path,
                    reference,
                    payload.CanonicalByteCount,
                    payload.SearchText);

            default:
                throw new InvalidDataException(
                    $"Payload kind '{payload.Kind}' cannot be restored to the clipboard.");
        }
    }

    private async Task<string> ResolveVerifiedExternalPathAsync(
        ClipboardHistoryExternalReference reference,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reference.RelativePath))
        {
            throw new InvalidDataException("External payload reference carries no relative path.");
        }

        if (Path.IsPathRooted(reference.RelativePath))
        {
            throw new InvalidDataException(
                "External payload reference must be relative to the configured Files root.");
        }

        string fullPath = Path.GetFullPath(Path.Combine(_filesRootPath, reference.RelativePath));
        ValidateManagedPath(fullPath);

        var file = new FileInfo(fullPath);
        if (!file.Exists)
        {
            throw new FileNotFoundException(
                "The external payload of this history entry is no longer present under the Files root.",
                fullPath);
        }

        if (file.Length != reference.SizeBytes)
        {
            throw new InvalidDataException(
                "External payload size does not match the stored reference.");
        }

        await using FileStream stream = new(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        string sha256 = Convert.ToHexString(hash).ToLowerInvariant();
        if (!string.Equals(sha256, reference.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "External payload content does not match the stored SHA-256 reference.");
        }

        return fullPath;
    }

    private void ValidateManagedPath(string fullPath)
    {
        if (!fullPath.StartsWith(_filesRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "External payload path escapes the configured Files root.");
        }

        EnsurePathIsNotReparsePoint(
            _filesRootPath,
            "Configured Files root is a reparse point during clipboard restore.");

        string? parentDirectory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(parentDirectory) ||
            !parentDirectory.StartsWith(_filesRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "External payload parent directory escapes the configured Files root.");
        }

        EnsurePathIsNotReparsePoint(
            parentDirectory,
            "External payload parent directory is a reparse point during clipboard restore.");
        EnsurePathIsNotReparsePoint(
            fullPath,
            "External payload file is a reparse point during clipboard restore.");
    }

    private static void EnsurePathIsNotReparsePoint(string path, string errorMessage)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new InvalidDataException(
                "External payload filesystem metadata could not be validated safely.",
                exception);
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException(errorMessage);
        }
    }
}
