namespace Clipensk.Core.Clipboard;

public interface IClipboardCapturePolicyRepository
{
    ValueTask<ClipboardCapturePolicy> GetGlobalPolicyAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The standalone policy of the application's user group, or <see langword="null"/> when the
    /// application is in the default group and the global policy applies
    /// (<c>docs/APPLICATION_GROUP_PROTOCOL.md</c> §1, invariant 4). The two are never merged.
    /// </summary>
    ValueTask<ClipboardCapturePolicy?> GetGroupPolicyAsync(
        Clipensk.Core.Applications.ApplicationId applicationId,
        CancellationToken cancellationToken = default);
}
