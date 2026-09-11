namespace Clipensk.Core.Clipboard;

public sealed record ApplicationDiscoveredCustomFormatEnableRequest(
    string FormatName,
    string FileExtension,
    ClipboardCapturePolicy Policy);

/// <summary>
/// Validates the product boundary for explicitly enabling one runtime-discovered
/// custom clipboard format for an application while preserving all unrelated
/// application policy overrides.
/// </summary>
public static class ApplicationDiscoveredCustomFormatEnableSetup
{
    public static ApplicationDiscoveredCustomFormatEnableRequest Create(
        ClipboardCapturePolicyRule applicationCapture,
        IReadOnlyCollection<string> discoveredFormatNames,
        IReadOnlyCollection<string> standardFormatNames,
        string formatName,
        string fileExtension,
        string? maxBytesText,
        IReadOnlyDictionary<string, ClipboardFormatCapturePolicy>? preservedFormats = null)
    {
        ArgumentNullException.ThrowIfNull(discoveredFormatNames);
        ArgumentNullException.ThrowIfNull(standardFormatNames);
        ArgumentException.ThrowIfNullOrWhiteSpace(formatName);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileExtension);

        var discovered = new HashSet<string>(StringComparer.Ordinal);
        foreach (string discoveredFormatName in discoveredFormatNames)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(discoveredFormatName);
            discovered.Add(discoveredFormatName);
        }
        if (!discovered.Contains(formatName))
        {
            throw new ArgumentException(
                "The clipboard format must be an exact runtime-discovered format for the selected application.",
                nameof(formatName));
        }

        var standard = new HashSet<string>(StringComparer.Ordinal);
        foreach (string standardFormatName in standardFormatNames)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(standardFormatName);
            standard.Add(standardFormatName);
        }
        if (standard.Contains(formatName))
        {
            throw new ArgumentException(
                "Standard clipboard formats must be edited through the standard application policy editor.",
                nameof(formatName));
        }
        if (!ClipboardCaptureFormatGuard.IsCaptureAllowed(formatName))
        {
            throw new ArgumentException(
                "The clipboard format is prohibited from capture.",
                nameof(formatName));
        }

        ClipboardCapturePolicy policy = ApplicationClipboardCapturePolicySetup.Create(
            applicationCapture,
            [
                new ApplicationClipboardFormatSetup(
                    formatName,
                    ClipboardCapturePolicyRule.Allow,
                    OverrideMaxBytes: true,
                    maxBytesText),
            ],
            preservedFormats);

        return new ApplicationDiscoveredCustomFormatEnableRequest(
            formatName,
            fileExtension,
            policy);
    }
}
