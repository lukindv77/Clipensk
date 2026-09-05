namespace Clipensk.Core.Clipboard;

public sealed class ClipboardCapturePipeline
{
    private readonly ClipboardCaptureSourceStage _sourceStage;
    private readonly ClipboardCapturePolicyResolutionStage _policyStage;
    private readonly ClipboardFormatDiscoveryStage _formatDiscoveryStage;
    private readonly ClipboardFormatSelectionStage _formatSelectionStage;

    public ClipboardCapturePipeline(
        ClipboardCaptureSourceStage sourceStage,
        ClipboardCapturePolicyResolutionStage policyStage,
        ClipboardFormatDiscoveryStage formatDiscoveryStage,
        ClipboardFormatSelectionStage formatSelectionStage)
    {
        _sourceStage = sourceStage ?? throw new ArgumentNullException(nameof(sourceStage));
        _policyStage = policyStage ?? throw new ArgumentNullException(nameof(policyStage));
        _formatDiscoveryStage = formatDiscoveryStage ?? throw new ArgumentNullException(nameof(formatDiscoveryStage));
        _formatSelectionStage = formatSelectionStage ?? throw new ArgumentNullException(nameof(formatSelectionStage));
    }

    public async ValueTask<ClipboardFormatSelection> ProcessNextAsync(
        CancellationToken cancellationToken = default)
    {
        ClipboardCaptureContext captureContext = await _sourceStage
            .ResolveNextAsync(cancellationToken)
            .ConfigureAwait(false);
        ClipboardCapturePolicyContext policyContext = await _policyStage
            .ResolveAsync(captureContext, cancellationToken)
            .ConfigureAwait(false);
        ClipboardFormatSnapshot formatSnapshot = _formatDiscoveryStage.Discover(policyContext);

        return _formatSelectionStage.Select(formatSnapshot);
    }
}
