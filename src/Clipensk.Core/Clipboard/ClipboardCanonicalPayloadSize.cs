using System.Text;

namespace Clipensk.Core.Clipboard;

public static class ClipboardCanonicalPayloadSize
{
    public static long MeasureUtf8Text(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Encoding.UTF8.GetByteCount(value);
    }

    public static long MeasureLink(Uri value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return MeasureUtf8Text(value.OriginalString);
    }

    public static long MeasureBinary(ReadOnlySpan<byte> value)
    {
        return value.Length;
    }

    public static bool IsWithinLimit(long canonicalByteCount, long? maxBytes)
    {
        if (canonicalByteCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(canonicalByteCount),
                canonicalByteCount,
                "Canonical byte count cannot be negative.");
        }

        if (maxBytes.HasValue && maxBytes.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxBytes),
                maxBytes,
                "MaxBytes must be positive when configured.");
        }

        return !maxBytes.HasValue || canonicalByteCount <= maxBytes.Value;
    }
}
