namespace Clipensk.Core.Applications;

public interface IApplicationIdentityRepository
{
    ValueTask<ApplicationIdentityAliasLookup> FindAliasesAsync(
        ApplicationIdentityObservation observation,
        CancellationToken cancellationToken = default);

    ValueTask<ApplicationId> CreateAndBindAsync(
        ApplicationIdentityObservation observation,
        ApplicationIdentityResolutionBasis basis,
        CancellationToken cancellationToken = default);

    ValueTask BindExecutablePathAliasAsync(
        ApplicationId applicationId,
        string executablePath,
        CancellationToken cancellationToken = default);
}
