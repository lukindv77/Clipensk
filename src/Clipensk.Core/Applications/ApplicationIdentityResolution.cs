namespace Clipensk.Core.Applications;

public enum ApplicationIdentityResolutionBasis
{
    PackagedApplicationUserModelId = 1,
    KnownExecutablePathAlias = 2,
    NewExecutablePathAlias = 3,
}

public readonly record struct ApplicationIdentityResolution(
    ApplicationId ApplicationId,
    ApplicationIdentityResolutionBasis Basis,
    bool WasCreated);
