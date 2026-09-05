namespace Clipensk.Core.Clipboard;

public interface IClipboardCapturePolicyRepository
{
    ValueTask<ClipboardCapturePolicy> GetGlobalPolicyAsync(
        CancellationToken cancellationToken = default);

    ValueTask<ClipboardCapturePolicy?> GetApplicationPolicyAsync(
        Clipensk.Core.Applications.ApplicationId applicationId,
        CancellationToken cancellationToken = default);
}
