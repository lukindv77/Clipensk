using System.Security.Cryptography;

namespace Clipensk.Storage.ExternalFiles;

public sealed class ExternalPayloadStore
{
    private readonly string _filesRootPath;
    private readonly string _filesRootPrefix;

    public ExternalPayloadStore(string filesRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filesRootPath);

        _filesRootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(filesRootPath));
        _filesRootPrefix = _filesRootPath + Path.DirectorySeparatorChar;
    }

    public async ValueTask<ExternalPayloadAddress> StoreNormalizedPngAsync(
        DateOnly firstStoredDate,
        ReadOnlyMemory<byte> pngBytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ExternalPayloadAddress address = ExternalPayloadAddressFactory.ForNormalizedPng(
            firstStoredDate,
            pngBytes.Span);

        await EnsureStoredAsync(address, pngBytes, cancellationToken).ConfigureAwait(false);
        return address;
    }

    public async ValueTask<ExternalPayloadAddress> StoreCustomBinaryAsync(
        DateOnly firstStoredDate,
        ReadOnlyMemory<byte> bytes,
        string extension = ".bin",
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ExternalPayloadAddress address = ExternalPayloadAddressFactory.ForCustomBinary(
            firstStoredDate,
            bytes.Span,
            extension);

        await EnsureStoredAsync(address, bytes, cancellationToken).ConfigureAwait(false);
        return address;
    }

    private async ValueTask EnsureStoredAsync(
        ExternalPayloadAddress address,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        string destinationPath = ResolveDestinationPath(address);
        if (await VerifyExistingIfPresentAsync(destinationPath, address, cancellationToken)
            .ConfigureAwait(false))
        {
            return;
        }

        string directoryPath = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidDataException("External payload path has no parent directory.");
        Directory.CreateDirectory(directoryPath);

        cancellationToken.ThrowIfCancellationRequested();
        string temporaryPath = Path.Combine(
            directoryPath,
            $".clipensk-{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                File.Move(temporaryPath, destinationPath);
                temporaryPath = string.Empty;
            }
            catch (IOException) when (File.Exists(destinationPath))
            {
                if (!await VerifyExistingIfPresentAsync(
                        destinationPath,
                        address,
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    throw;
                }
            }
        }
        finally
        {
            if (!string.IsNullOrEmpty(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // Best-effort cleanup only. The final content-addressed file is never overwritten.
                }
                catch (UnauthorizedAccessException)
                {
                    // Best-effort cleanup only. The final content-addressed file is never overwritten.
                }
            }
        }
    }

    private string ResolveDestinationPath(ExternalPayloadAddress address)
    {
        string destinationPath = Path.GetFullPath(
            Path.Combine(_filesRootPath, address.RelativePath));

        if (!destinationPath.StartsWith(_filesRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "External payload address escapes the configured Files root.");
        }

        return destinationPath;
    }

    private static async ValueTask<bool> VerifyExistingIfPresentAsync(
        string destinationPath,
        ExternalPayloadAddress address,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                destinationPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            if (stream.Length != address.SizeBytes)
            {
                throw new InvalidDataException(
                    "Existing external payload size does not match its content address.");
            }

            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            string sha256 = Convert.ToHexString(hash).ToLowerInvariant();
            if (!string.Equals(sha256, address.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Existing external payload hash does not match its content address.");
            }

            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }
}
