using System.Globalization;

namespace Clipensk.Core.Clipboard;

public sealed record ApplicationClipboardFormatSetup(
    string FormatName,
    ClipboardCapturePolicyRule? Capture,
    bool OverrideMaxBytes,
    string? MaxBytesText);

/// <summary>
/// Validates and composes per-application clipboard policy overrides while preserving
/// existing formats that are not explicitly edited by the caller.
/// </summary>
public static class ApplicationClipboardCapturePolicySetup
{
    public static ClipboardCapturePolicy Create(
        ClipboardCapturePolicyRule? capture,
        IReadOnlyList<ApplicationClipboardFormatSetup> formats,
        IReadOnlyDictionary<string, ClipboardFormatCapturePolicy>? preservedFormats = null)
    {
        ArgumentNullException.ThrowIfNull(formats);

        ClipboardCapturePolicyRule applicationRule = RequireApplicationRule(capture, nameof(capture));
        var rules = new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal);
        if (preservedFormats is not null)
        {
            foreach ((string formatName, ClipboardFormatCapturePolicy formatPolicy) in preservedFormats)
            {
                rules.Add(formatName, formatPolicy);
            }
        }

        var editedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (ApplicationClipboardFormatSetup format in formats)
        {
            ArgumentNullException.ThrowIfNull(format);
            ArgumentException.ThrowIfNullOrWhiteSpace(format.FormatName);
            if (!editedNames.Add(format.FormatName))
            {
                throw new ArgumentException("Clipboard format names must be unique.", nameof(formats));
            }

            ClipboardCapturePolicyRule formatRule = RequireApplicationRule(format.Capture, nameof(formats));
            long? maxBytes = format.OverrideMaxBytes
                ? ParsePositiveByteLimit(format.MaxBytesText, nameof(formats))
                : null;

            if (formatRule == ClipboardCapturePolicyRule.Inherit && !maxBytes.HasValue)
            {
                rules.Remove(format.FormatName);
            }
            else
            {
                rules[format.FormatName] = new ClipboardFormatCapturePolicy(formatRule, maxBytes);
            }
        }

        return new ClipboardCapturePolicy(applicationRule, rules);
    }

    private static ClipboardCapturePolicyRule RequireApplicationRule(
        ClipboardCapturePolicyRule? rule,
        string parameterName) =>
        rule is ClipboardCapturePolicyRule.Inherit or ClipboardCapturePolicyRule.Allow or ClipboardCapturePolicyRule.Deny
            ? rule.Value
            : throw new ArgumentException("Choose Inherit, Allow, or Deny.", parameterName);

    private static long ParsePositiveByteLimit(string? text, string parameterName)
    {
        if (!long.TryParse(
                text?.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long size) ||
            size <= 0)
        {
            throw new ArgumentException("Size must be a positive integer number of bytes.", parameterName);
        }

        return size;
    }
}
