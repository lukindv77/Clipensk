using System.Globalization;

namespace Clipensk.Core.Clipboard;

public sealed record GlobalClipboardFormatSetup(
    string FormatName,
    ClipboardCapturePolicyRule? Capture,
    bool? LimitEnabled,
    string? MaxBytesText);

/// <summary>Validates explicit user choices without selecting rules or size defaults.</summary>
public static class GlobalClipboardCapturePolicySetup
{
    public static ClipboardCapturePolicy Create(
        ClipboardCapturePolicyRule? capture,
        IReadOnlyList<GlobalClipboardFormatSetup> formats)
    {
        ArgumentNullException.ThrowIfNull(formats);
        ClipboardCapturePolicyRule globalRule = RequireExplicitRule(capture);
        var rules = new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal);
        foreach (GlobalClipboardFormatSetup format in formats)
        {
            ArgumentNullException.ThrowIfNull(format);
            ArgumentException.ThrowIfNullOrWhiteSpace(format.FormatName);
            ClipboardCapturePolicyRule rule = RequireExplicitRule(format.Capture);
            long? maxBytes = null;
            if (rule == ClipboardCapturePolicyRule.Allow)
            {
                if (!format.LimitEnabled.HasValue)
                {
                    throw new ArgumentException("Choose a size limit or explicitly choose no limit.", nameof(formats));
                }
                if (format.LimitEnabled.Value)
                {
                    if (!long.TryParse(format.MaxBytesText?.Trim(), NumberStyles.None,
                        CultureInfo.InvariantCulture, out long size) || size <= 0)
                    {
                        throw new ArgumentException("Size must be a positive integer number of bytes.", nameof(formats));
                    }
                    maxBytes = size;
                }
            }
            if (!rules.TryAdd(format.FormatName, new ClipboardFormatCapturePolicy(rule, maxBytes)))
            {
                throw new ArgumentException("Clipboard format names must be unique.", nameof(formats));
            }
        }
        return new ClipboardCapturePolicy(globalRule, rules);
    }

    private static ClipboardCapturePolicyRule RequireExplicitRule(ClipboardCapturePolicyRule? rule) =>
        rule is ClipboardCapturePolicyRule.Allow or ClipboardCapturePolicyRule.Deny
            ? rule.Value
            : throw new ArgumentException("Choose an explicit Allow or Deny rule.", nameof(rule));
}
