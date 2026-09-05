namespace Clipensk.Core.Clipboard;

public sealed class ClipboardCapturePolicyEvaluator
{
    public ClipboardCapturePolicy Merge(
        ClipboardCapturePolicy globalPolicy,
        ClipboardCapturePolicy? applicationPolicy)
    {
        ArgumentNullException.ThrowIfNull(globalPolicy);

        ClipboardCapturePolicyRule capture = applicationPolicy is null
            ? globalPolicy.Capture
            : ResolveRule(globalPolicy.Capture, applicationPolicy.Capture);

        var formatNames = new HashSet<string>(globalPolicy.Formats.Keys, StringComparer.Ordinal);
        if (applicationPolicy is not null)
        {
            formatNames.UnionWith(applicationPolicy.Formats.Keys);
        }

        var effectiveFormats = new Dictionary<string, ClipboardFormatCapturePolicy>(
            formatNames.Count,
            StringComparer.Ordinal);

        foreach (string formatName in formatNames)
        {
            bool hasGlobal = globalPolicy.Formats.TryGetValue(formatName, out ClipboardFormatCapturePolicy globalFormat);
            ClipboardFormatCapturePolicy applicationFormat = default;
            bool hasApplication = applicationPolicy is not null
                && applicationPolicy.Formats.TryGetValue(formatName, out applicationFormat);

            ClipboardCapturePolicyRule globalRule = hasGlobal
                ? globalFormat.Capture
                : ClipboardCapturePolicyRule.Inherit;
            ClipboardCapturePolicyRule applicationRule = hasApplication
                ? applicationFormat.Capture
                : ClipboardCapturePolicyRule.Inherit;
            long? maxBytes = hasApplication && applicationFormat.MaxBytes.HasValue
                ? applicationFormat.MaxBytes
                : hasGlobal
                    ? globalFormat.MaxBytes
                    : null;

            effectiveFormats.Add(
                formatName,
                new ClipboardFormatCapturePolicy(
                    ResolveRule(globalRule, applicationRule),
                    maxBytes));
        }

        return new ClipboardCapturePolicy(capture, effectiveFormats);
    }

    private static ClipboardCapturePolicyRule ResolveRule(
        ClipboardCapturePolicyRule baseRule,
        ClipboardCapturePolicyRule overrideRule)
    {
        return overrideRule == ClipboardCapturePolicyRule.Inherit
            ? baseRule
            : overrideRule;
    }
}
