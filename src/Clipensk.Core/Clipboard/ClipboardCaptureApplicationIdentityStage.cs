using Clipensk.Core.Applications;

namespace Clipensk.Core.Clipboard;

public sealed class ClipboardCaptureApplicationIdentityStage
{
    private readonly IApplicationIdentityRegistry _identityRegistry;

    public ClipboardCaptureApplicationIdentityStage(IApplicationIdentityRegistry identityRegistry)
    {
        _identityRegistry = identityRegistry ?? throw new ArgumentNullException(nameof(identityRegistry));
    }

    public async ValueTask<ClipboardCaptureContext> ResolveAsync(
        ClipboardCaptureContext captureContext,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (captureContext.SourceApplicationId is not null)
        {
            return captureContext;
        }

        if (captureContext.SourceApplication is not { } sourceApplication)
        {
            return captureContext;
        }

        var observation = new ApplicationIdentityObservation(
            sourceApplication.ApplicationUserModelId,
            sourceApplication.ExecutablePath);
        if (!observation.HasResolvableEvidence)
        {
            return captureContext;
        }

        ApplicationIdentityResolution? resolution = await _identityRegistry
            .ResolveOrCreateAsync(observation, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return resolution is null
            ? captureContext
            : captureContext with { SourceApplicationId = resolution.ApplicationId };
    }
}
