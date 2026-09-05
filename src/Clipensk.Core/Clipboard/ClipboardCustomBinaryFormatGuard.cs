namespace Clipensk.Core.Clipboard;

public static class ClipboardCustomBinaryFormatGuard
{
    private static readonly HashSet<string> ProhibitedFormatNames = new(
        StringComparer.Ordinal)
    {
        "RiffAudio",
        "WaveAudio",
        "FileContents",
    };

    public static bool IsAllowedCandidate(string formatName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formatName);
        return !ProhibitedFormatNames.Contains(formatName);
    }
}
