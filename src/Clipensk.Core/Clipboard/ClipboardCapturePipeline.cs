namespace Clipensk.Core.Clipboard;

public sealed class ClipboardCapturePipeline
{
    private readonly ClipboardCaptureSourceStage _sourceStage;
    private readonly ClipboardCaptureApplicationIdentityStage? _applicationIdentityStage;
    private readonly ClipboardCapturePolicyResolutionStage _policyStage;
    private readonly ClipboardFormatDiscoveryStage _formatDiscoveryStage;
    private readonly ClipboardApplicationDiscoveredFormatObservationStage? _applicationDiscoveredFormatObservationStage;
    private readonly ClipboardFormatSelectionStage _formatSelectionStage;

    public ClipboardCapturePipeline(
        ClipboardCaptureSourceStage sourceStage,
        ClipboardCapturePolicyResolutionStage policyStage,
        ClipboardFormatDiscoveryStage formatDiscoveryStage,
        ClipboardFormatSelectionStage formatSelectionStage)
    {
        _sourceStage = sourceStage ?? throw new ArgumentNullException(nameof(sourceStage));
        _applicationIdentityStage = null;
        _policyStage = policyStage ?? throw new ArgumentNullException(nameof(policyStage));
        _formatDiscoveryStage = formatDiscoveryStage ?? throw new ArgumentNullException(nameof(formatDiscoveryStage));
        _applicationDiscoveredFormatObservationStage = null;
        _formatSelectionStage = formatSelectionStage ?? throw new ArgumentNullException(nameof(formatSelectionStage));
    }

    public ClipboardCapturePipeline(
        ClipboardCaptureSourceStage sourceStage,
        ClipboardCaptureApplicationIdentityStage applicationIdentityStage,
        ClipboardCapturePolicyResolutionStage policyStage,
        ClipboardFormatDiscoveryStage formatDiscoveryStage,
        ClipboardFormatSelectionStage formatSelectionStage)
    {
        _sourceStage = sourceStage ?? throw new ArgumentNullException(nameof(sourceStage));
        _applicationIdentityStage = applicationIdentityStage
            ?? throw new ArgumentNullException(nameof(applicationIdentityStage));
        _policyStage = policyStage ?? throw new ArgumentNullException(nameof(policyStage));
        _formatDiscoveryStage = formatDiscoveryStage ?? throw new ArgumentNullException(nameof(formatDiscoveryStage));
        _applicationDiscoveredFormatObservationStage = null;
        _formatSelectionStage = formatSelectionStage ?? throw new ArgumentNullException(nameof(formatSelectionStage));
    }

    public ClipboardCapturePipeline(
        ClipboardCaptureSourceStage sourceStage,
        ClipboardCaptureApplicationIdentityStage applicationIdentityStage,
        ClipboardCapturePolicyResolutionStage policyStage,
        ClipboardFormatDiscoveryStage formatDiscoveryStage,
        ClipboardApplicationDiscoveredFormatObservationStage applicationDiscoveredFormatObservationStage,
        ClipboardFormatSelectionStage formatSelectionStage)
    {
        _sourceStage = sourceStage ?? throw new ArgumentNullException(nameof(sourceStage));
        _applicationIdentityStage = applicationIdentityStage
            ?? throw new ArgumentNullException(nameof(applicationIdentityStage));
        _policyStage = policyStage ?? throw new ArgumentNullException(nameof(policyStage));
        _formatDiscoveryStage = formatDiscoveryStage ?? throw new ArgumentNullException(nameof(formatDiscoveryStage));
        _applicationDiscoveredFormatObservationStage = applicationDiscoveredFormatObservationStage
            ?? throw new ArgumentNullException(nameof(applicationDiscoveredFormatObservationStage));
        _formatSelectionStage = formatSelectionStage ?? throw new ArgumentNullException(nameof(formatSelectionStage));
    }

    public async ValueTask<ClipboardFormatSelection> ProcessNextAsync(
        CancellationToken cancellationToken = default)
    {
        ClipboardCaptureContext captureContext = await _sourceStage
            .ResolveNextAsync(cancellationToken)
            .ConfigureAwait(false);

        if (_applicationIdentityStage is not null)
        {
            captureContext = await _applicationIdentityStage
                .ResolveAsync(captureContext, cancellationToken)
                .ConfigureAwait(false);
        }

        ClipboardCapturePolicyContext policyContext = await _policyStage
            .ResolveAsync(captureContext, cancellationToken)
            .ConfigureAwait(false);
        ClipboardFormatSnapshot formatSnapshot = _formatDiscoveryStage.Discover(policyContext);

        if (_applicationDiscoveredFormatObservationStage is not null)
        {
            await _applicationDiscoveredFormatObservationStage
                .ObserveAsync(formatSnapshot, cancellationToken)
                .ConfigureAwait(false);
        }

        return _formatSelectionStage.Select(formatSnapshot);
    }
}
