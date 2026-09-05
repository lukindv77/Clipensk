namespace Clipensk.Core.Clipboard;

public sealed class GlobalOnlyClipboardCapturePolicyRepository : IClipboardCapturePolicyRepository
{
    private readonly ClipboardCapturePolicy _globalPolicy;

    public GlobalOnlyClipboardCapturePolicyRepository(ClipboardCapturePolicy globalPolicy)
    {
        _globalPolicy = globalPolicy ?? throw new ArgumentNullException(nameof(globalPolicy));
    }

    public ValueTask<ClipboardCapturePolicy> GetGlobalPolicyAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_globalPolicy);
    }

    public ValueTask<ClipboardCapturePolicy?> GetApplicationPolicyAsync(
        Clipensk.Core.Applications.ApplicationId applicationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(applicationId);
        cancellationToken.ThrowIfCancellationRequested();

        // This repository intentionally exposes no per-application overrides even when
        // the caller has already resolved a durable Clipensk ApplicationId.
        return ValueTask.FromResult<ClipboardCapturePolicy?>(null);
    }
}
