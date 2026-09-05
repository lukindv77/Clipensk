namespace Clipensk.Core.Applications;

public interface IApplicationIdentityRegistry
{
    ValueTask<ApplicationIdentityResolution?> ResolveOrCreateAsync(
        ApplicationIdentityObservation observation,
        CancellationToken cancellationToken = default);
}
