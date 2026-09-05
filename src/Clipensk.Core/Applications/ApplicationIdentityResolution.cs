namespace Clipensk.Core.Applications;

public enum ApplicationIdentityResolutionBasis
{
    PackagedApplicationUserModelId = 1,
    ExecutablePathAlias = 2,
}

public readonly record struct ApplicationIdentityResolution(
    ApplicationId ApplicationId,
    ApplicationIdentityResolutionBasis Basis,
    bool WasCreated);
