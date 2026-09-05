namespace Clipensk.Core.Applications;

public enum ApplicationIdentityResolutionBasis
{
    PackagedApplicationUserModelId = 1,
    ExecutablePathAlias = 2,
}

public sealed record ApplicationIdentityResolution
{
    public ApplicationIdentityResolution(
        ApplicationId applicationId,
        ApplicationIdentityResolutionBasis basis,
        bool wasCreated)
    {
        ApplicationId = applicationId ?? throw new ArgumentNullException(nameof(applicationId));
        if (!Enum.IsDefined(basis))
        {
            throw new ArgumentOutOfRangeException(nameof(basis));
        }

        Basis = basis;
        WasCreated = wasCreated;
    }

    public ApplicationId ApplicationId { get; }

    public ApplicationIdentityResolutionBasis Basis { get; }

    public bool WasCreated { get; }
}
