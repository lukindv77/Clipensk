namespace Clipensk.Core.Clipboard;

/// <summary>
/// The policies capture may apply to one clipboard change: the global policy (the default group's)
/// and, for an application in a user group, that group's standalone policy, which replaces the
/// global one.
/// </summary>
public sealed record ClipboardCapturePolicySet
{
    public ClipboardCapturePolicySet(
        ClipboardCapturePolicy globalPolicy,
        ClipboardCapturePolicy? groupPolicy = null)
    {
        GlobalPolicy = globalPolicy ?? throw new ArgumentNullException(nameof(globalPolicy));
        GroupPolicy = groupPolicy;
    }

    public ClipboardCapturePolicy GlobalPolicy { get; }

    public ClipboardCapturePolicy? GroupPolicy { get; }
}
