namespace Clipensk.Core.Clipboard;

/// <summary>
/// Decides which saved representations survive a history purge, per
/// <c>docs/APPLICATION_GROUP_PROTOCOL.md</c> §5.2. It mirrors the capture-time format selection:
/// a representation survives only when the effective policy allows capture and explicitly allows
/// its exact format name. <c>MaxBytes</c> is deliberately not part of the rule — the size limit
/// gates capture only.
/// </summary>
public sealed class ClipboardHistoryPurgeRule : IEquatable<ClipboardHistoryPurgeRule>
{
    private readonly HashSet<string> _allowedFormats;

    public ClipboardHistoryPurgeRule(
        ClipboardCapturePolicyRule capture,
        IEnumerable<string> allowedFormats)
    {
        ArgumentNullException.ThrowIfNull(allowedFormats);
        if (capture is not (ClipboardCapturePolicyRule.Allow or ClipboardCapturePolicyRule.Deny))
        {
            throw new ArgumentOutOfRangeException(
                nameof(capture),
                capture,
                "An effective capture rule must be explicit Allow or Deny.");
        }

        _allowedFormats = new HashSet<string>(StringComparer.Ordinal);
        foreach (string formatName in allowedFormats)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(formatName);
            if (!_allowedFormats.Add(formatName))
            {
                throw new ArgumentException("Allowed format names must be unique.", nameof(allowedFormats));
            }
        }

        Capture = capture;
        AllowedFormats = _allowedFormats.OrderBy(static name => name, StringComparer.Ordinal).ToArray();
    }

    public ClipboardCapturePolicyRule Capture { get; }

    /// <summary>Allowed format names in ordinal order, for a stable persisted snapshot.</summary>
    public IReadOnlyList<string> AllowedFormats { get; }

    public static ClipboardHistoryPurgeRule FromEffectivePolicy(ClipboardCapturePolicy effectivePolicy)
    {
        ArgumentNullException.ThrowIfNull(effectivePolicy);
        return new ClipboardHistoryPurgeRule(
            effectivePolicy.Capture,
            effectivePolicy.Formats
                .Where(static pair => pair.Value.Capture == ClipboardCapturePolicyRule.Allow)
                .Select(static pair => pair.Key));
    }

    public bool Retains(string formatName)
    {
        ArgumentNullException.ThrowIfNull(formatName);
        return Capture == ClipboardCapturePolicyRule.Allow && _allowedFormats.Contains(formatName);
    }

    /// <summary>Two rules are equal when their capture rule and allowed format names match exactly.</summary>
    public bool Equals(ClipboardHistoryPurgeRule? other) =>
        other is not null &&
        Capture == other.Capture &&
        AllowedFormats.SequenceEqual(other.AllowedFormats, StringComparer.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as ClipboardHistoryPurgeRule);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Capture);
        foreach (string formatName in AllowedFormats)
        {
            hash.Add(formatName, StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }
}
