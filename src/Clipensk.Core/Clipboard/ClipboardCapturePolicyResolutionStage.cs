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

        ClipboardCapturePolicy effectivePolicy = _evaluator.Merge(
            policies.GlobalPolicy,
            policies.ApplicationPolicy);

        return new ClipboardCapturePolicyContext(captureContext, effectivePolicy);
    }
}
