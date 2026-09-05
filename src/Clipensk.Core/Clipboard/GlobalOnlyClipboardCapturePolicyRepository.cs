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
        ClipboardSourceApplication sourceApplication,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceApplication);
        cancellationToken.ThrowIfCancellationRequested();

        // Runtime source metadata (PID/path) is intentionally not treated as a durable
        // application identity. Per-application policy remains unavailable until the
        // persistent application identity contract is defined.
        return ValueTask.FromResult<ClipboardCapturePolicy?>(null);
    }
}
