namespace Clipensk.Core.Clipboard;

public sealed class RepositoryClipboardCapturePolicyProvider : IClipboardCapturePolicyProvider
{
    private readonly IClipboardCapturePolicyRepository _repository;

    public RepositoryClipboardCapturePolicyProvider(IClipboardCapturePolicyRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async ValueTask<ClipboardCapturePolicySet> GetPoliciesAsync(
        ClipboardCaptureContext captureContext,
        CancellationToken cancellationToken = default)
    {
        ClipboardCapturePolicy globalPolicy = await _repository
            .GetGlobalPolicyAsync(cancellationToken)
            .ConfigureAwait(false);

        ClipboardCapturePolicy? applicationPolicy = null;
        if (captureContext.SourceApplicationId is { } sourceApplicationId)
        {
            applicationPolicy = await _repository
                .GetApplicationPolicyAsync(sourceApplicationId, cancellationToken)
                .ConfigureAwait(false);
        }

        return new ClipboardCapturePolicySet(globalPolicy, applicationPolicy);
    }
}
