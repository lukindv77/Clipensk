namespace Clipensk.Core.Clipboard;

/// <summary>Storage-scoped policy setup; absence is not an implicit capture decision.</summary>
public interface IGlobalClipboardCapturePolicyRepository
{
    ValueTask<ClipboardCapturePolicy?> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically persists the first explicit policy. An existing policy cannot be replaced here:
    /// subsequent changes require the separate policy-cleanup lifecycle.
    /// </summary>
    ValueTask InitializeAsync(
        ClipboardCapturePolicy policy,
        CancellationToken cancellationToken = default);
}
