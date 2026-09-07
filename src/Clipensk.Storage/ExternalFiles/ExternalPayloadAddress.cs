using System.Security.Cryptography;

namespace Clipensk.Storage.ExternalFiles;

public sealed record ExternalPayloadAddress(
    string Sha256,
    string RelativePath,
    long SizeBytes);

public static class ExternalPayloadAddressFactory
{
    public static ExternalPayloadAddress ForNormalizedPng(DateOnly firstStoredDate, ReadOnlySpan<byte> pngBytes)
    {
        return Create(firstStoredDate, pngBytes, ".png");
    }

    public static ExternalPayloadAddress ForCustomBinary(
        DateOnly firstStoredDate,
        ReadOnlySpan<byte> bytes,
        string extension = ".bin")
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".bin";
        }

        return Create(firstStoredDate, bytes, NormalizeCustomBinaryExtension(extension));
    }

    public static string NormalizeCustomBinaryExtension(string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        extension = extension.Trim();

        if (!extension.StartsWith(".", StringComparison.Ordinal))
        {
            extension = "." + extension;
        }

        if (extension.Length == 1 ||
            extension.Contains('/') ||
            extension.Contains('\\') ||
            extension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException(
                "Custom binary extension must be a single valid file extension.",
                nameof(extension));
        }

        return extension.ToLowerInvariant();
    }

    private static ExternalPayloadAddress Create(DateOnly firstStoredDate, ReadOnlySpan<byte> bytes, string extension)
    {
        byte[] hash = SHA256.HashData(bytes);
        string sha256 = Convert.ToHexString(hash).ToLowerInvariant();
        string relativePath = Path.Combine(
            firstStoredDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            sha256 + extension);

        return new ExternalPayloadAddress(sha256, relativePath, bytes.Length);
    }
}
