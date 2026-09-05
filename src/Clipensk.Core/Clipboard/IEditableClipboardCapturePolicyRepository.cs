using Clipensk.Core.Applications;

namespace Clipensk.Core.Clipboard;

public interface IEditableClipboardCapturePolicyRepository : IClipboardCapturePolicyRepository
{
    ValueTask SetApplicationPolicyAsync(
        ApplicationId applicationId,
        ClipboardCapturePolicy policy,
        CancellationToken cancellationToken = default);

    ValueTask DeleteApplicationPolicyAsync(
        ApplicationId applicationId,
        CancellationToken cancellationToken = default);
}
