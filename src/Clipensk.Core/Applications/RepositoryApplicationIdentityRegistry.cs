namespace Clipensk.Core.Applications;

public sealed class RepositoryApplicationIdentityRegistry : IApplicationIdentityRegistry
{
    private readonly IApplicationIdentityRepository _repository;

    public RepositoryApplicationIdentityRegistry(IApplicationIdentityRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async ValueTask<ApplicationIdentityResolution?> ResolveOrCreateAsync(
        ApplicationIdentityObservation observation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!observation.HasResolvableEvidence)
        {
            return null;
        }

        ApplicationIdentityAliasLookup lookup = await _repository
            .FindAliasesAsync(observation, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        ApplicationId? aumidApplicationId = lookup.ApplicationUserModelIdApplicationId;
        ApplicationId? pathApplicationId = lookup.ExecutablePathApplicationId;

        if (aumidApplicationId is not null)
        {
            if (pathApplicationId is not null && pathApplicationId != aumidApplicationId)
            {
                throw CreateConflict(observation, lookup);
            }

            if (!string.IsNullOrWhiteSpace(observation.ExecutablePath) &&
                pathApplicationId is null)
            {
                await _repository
                    .BindExecutablePathAliasAsync(
                        aumidApplicationId,
                        observation.ExecutablePath,
                        cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            return new ApplicationIdentityResolution(
                aumidApplicationId,
                ApplicationIdentityResolutionBasis.PackagedApplicationUserModelId,
                wasCreated: false);
        }

        if (!string.IsNullOrWhiteSpace(observation.ApplicationUserModelId))
        {
            if (pathApplicationId is not null)
            {
                throw CreateConflict(observation, lookup);
            }

            ApplicationId created = await _repository
                .CreateAndBindAsync(
                    observation,
                    ApplicationIdentityResolutionBasis.PackagedApplicationUserModelId,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new ApplicationIdentityResolution(
                created,
                ApplicationIdentityResolutionBasis.PackagedApplicationUserModelId,
                wasCreated: true);
        }

        if (pathApplicationId is not null)
        {
            return new ApplicationIdentityResolution(
                pathApplicationId,
                ApplicationIdentityResolutionBasis.ExecutablePathAlias,
                wasCreated: false);
        }

        ApplicationId pathCreated = await _repository
            .CreateAndBindAsync(
                observation,
                ApplicationIdentityResolutionBasis.ExecutablePathAlias,
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new ApplicationIdentityResolution(
            pathCreated,
            ApplicationIdentityResolutionBasis.ExecutablePathAlias,
            wasCreated: true);
    }

    private static ApplicationIdentityConflictException CreateConflict(
        ApplicationIdentityObservation observation,
        ApplicationIdentityAliasLookup lookup)
    {
        return new ApplicationIdentityConflictException(
            observation,
            lookup.ApplicationUserModelIdApplicationId,
            lookup.ExecutablePathApplicationId);
    }
}
