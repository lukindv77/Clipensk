namespace Clipensk.Core.Applications;

public enum ApplicationIdentityResolutionBasis
{
    PackagedApplicationUserModelId = 1,
    ExecutablePathAlias = 2,
}

public sealed record ApplicationIdentityResolution(
    ApplicationId ApplicationId,
    ApplicationIdentityResolutionBasis Basis,
    bool WasCreated);
