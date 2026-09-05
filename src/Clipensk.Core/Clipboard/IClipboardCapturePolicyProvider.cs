namespace Clipensk.Core.Clipboard;

public interface IClipboardCapturePolicyProvider
{
    ValueTask<ClipboardCapturePolicySet> GetPoliciesAsync(
        ClipboardCaptureContext captureContext,
        CancellationToken cancellationToken = default);
}
