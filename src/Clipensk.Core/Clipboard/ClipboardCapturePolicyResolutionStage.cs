namespace Clipensk.Core.Clipboard;

public sealed class ClipboardCapturePolicyResolutionStage
{
    private readonly IClipboardCapturePolicyProvider _provider;
    private readonly ClipboardCapturePolicyEvaluator _evaluator;

    public ClipboardCapturePolicyResolutionStage(
        IClipboardCapturePolicyProvider provider,
        ClipboardCapturePolicyEvaluator evaluator)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
    }

    public async ValueTask<ClipboardCapturePolicyContext> ResolveAsync(
        ClipboardCaptureContext captureContext,
        CancellationToken cancellationToken = default)
    {
        ClipboardCapturePolicySet policies = await _provider
            .GetPoliciesAsync(captureContext, cancellationToken)
            .ConfigureAwait(false);

        // A group policy replaces the global one instead of refining it; merging it with no
        // override only normalizes it the same way the global policy is normalized.
        ClipboardCapturePolicy effectivePolicy = _evaluator.Merge(
            policies.GroupPolicy ?? policies.GlobalPolicy,
            applicationPolicy: null);

        return new ClipboardCapturePolicyContext(captureContext, effectivePolicy);
    }
}
