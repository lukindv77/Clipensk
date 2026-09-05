namespace Clipensk.Core.Clipboard;

public sealed class ClipboardCapturePolicyStage
{
    private readonly ClipboardCapturePolicyEvaluator _evaluator;

    public ClipboardCapturePolicyStage(ClipboardCapturePolicyEvaluator evaluator)
    {
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
    }

    public ClipboardCapturePolicyContext Evaluate(
        ClipboardCaptureContext captureContext,
        ClipboardCapturePolicy globalPolicy,
        ClipboardCapturePolicy? applicationPolicy = null)
    {
        ClipboardCapturePolicy policy = _evaluator.Merge(globalPolicy, applicationPolicy);
        return new ClipboardCapturePolicyContext(captureContext, policy);
    }
}
