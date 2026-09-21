using System.Globalization;

namespace Clipensk.Core.Clipboard;

/// <summary>
/// Converts between the whole-kilobyte size limit shown in Settings and the exact
/// <see cref="ClipboardFormatCapturePolicy.MaxBytes"/> byte count SQLite persists and the capture
/// pipeline enforces, per the product decision that format size limits are edited in kilobytes.
/// </summary>
public static class ClipboardFormatSizeLimit
{
    public const long BytesPerKilobyte = 1024L;

    private const long MaxRepresentableKilobytes = long.MaxValue / BytesPerKilobyte;

    /// <summary>
    /// Parses a positive whole number of kilobytes from settings-editing text and returns the
    /// equivalent byte count. Rejects anything that is not a plain positive integer — no decimals,
    /// thousands separators, or scientific notation — so a malformed limit fails loudly instead of
    /// silently becoming a different number.
    /// </summary>
    public static long ParseKilobytesAsBytes(string? text, string parameterName)
    {
        if (!long.TryParse(
                text?.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long kilobytes) ||
            kilobytes <= 0)
        {
            throw new ArgumentException("Size must be a positive integer number of kilobytes.", parameterName);
        }

        if (kilobytes > MaxRepresentableKilobytes)
        {
            throw new ArgumentException("Size is too large to express in bytes.", parameterName);
        }

        return kilobytes * BytesPerKilobyte;
    }

    /// <summary>
    /// Maps a stored byte limit back to whole kilobytes for display, rounding up. A stored value
    /// that already came from this converter is always an exact multiple of a kilobyte; rounding up
    /// rather than down means that if a value ever isn't (e.g. from data written before this
    /// conversion existed), simply re-saving an unchanged display never silently tightens a
    /// previously configured limit.
    /// </summary>
    public static long BytesToKilobytesRoundedUp(long maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        return (maxBytes + BytesPerKilobyte - 1) / BytesPerKilobyte;
    }
}
