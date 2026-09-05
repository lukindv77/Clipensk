namespace Clipensk.Core.Clipboard;

public static class ClipboardCaptureFormatGuard
{
    private static readonly HashSet<string> ProhibitedFormatNames = new(
        StringComparer.Ordinal)
    {
        "RiffAudio",
        "WaveAudio",
        "FileContents",
    };

    public static bool IsCaptureAllowed(string formatName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formatName);
        return !ProhibitedFormatNames.Contains(formatName);
    }
}
