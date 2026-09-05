namespace Clipensk.Core.Clipboard;

public interface IClipboardCapturePolicyRepository
{
    ValueTask<ClipboardCapturePolicy> GetGlobalPolicyAsync(
        CancellationToken cancellationToken = default);

    ValueTask<ClipboardCapturePolicy?> GetApplicationPolicyAsync(
        ClipboardSourceApplication sourceApplication,
        CancellationToken cancellationToken = default);
}
