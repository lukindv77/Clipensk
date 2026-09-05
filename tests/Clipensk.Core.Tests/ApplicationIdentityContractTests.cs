using Clipensk.Core.Applications;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ApplicationIdentityContractTests
{
    [Fact]
    public void ApplicationId_RejectsEmptyGuid()
    {
        Assert.Throws<ArgumentException>(() => new ApplicationId(Guid.Empty));
    }

    [Fact]
    public void ApplicationId_NewCreatesNonEmptyDurableKey()
    {
        ApplicationId first = ApplicationId.New();
        ApplicationId second = ApplicationId.New();

        Assert.NotEqual(Guid.Empty, first.Value);
        Assert.NotEqual(Guid.Empty, second.Value);
        Assert.NotEqual(first, second);
        Assert.Equal(first.Value.ToString("D"), first.ToString());
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("", "", false)]
    [InlineData("  ", "  ", false)]
    [InlineData("Contoso.App_123!App", null, true)]
    [InlineData(null, "C:\\Apps\\Contoso.exe", true)]
    [InlineData("Contoso.App_123!App", "C:\\Apps\\Contoso.exe", true)]
    public void Observation_ReportsWhetherResolvableEvidenceExists(
        string? applicationUserModelId,
        string? executablePath,
        bool expected)
    {
        var observation = new ApplicationIdentityObservation(
            applicationUserModelId,
            executablePath);

        Assert.Equal(expected, observation.HasResolvableEvidence);
    }

    [Fact]
    public void Resolution_KeepsDurableKeySeparateFromResolutionEvidence()
    {
        ApplicationId id = ApplicationId.New();
        var packaged = new ApplicationIdentityResolution(
            id,
            ApplicationIdentityResolutionBasis.PackagedApplicationUserModelId,
            wasCreated: false);
        var unpackaged = new ApplicationIdentityResolution(
            id,
            ApplicationIdentityResolutionBasis.ExecutablePathAlias,
            wasCreated: true);

        Assert.Equal(id, packaged.ApplicationId);
        Assert.Equal(id, unpackaged.ApplicationId);
        Assert.Equal(ApplicationIdentityResolutionBasis.PackagedApplicationUserModelId, packaged.Basis);
        Assert.Equal(ApplicationIdentityResolutionBasis.ExecutablePathAlias, unpackaged.Basis);
        Assert.False(packaged.WasCreated);
        Assert.True(unpackaged.WasCreated);
    }

    [Fact]
    public void Resolution_RejectsMissingDurableKey()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ApplicationIdentityResolution(
                null!,
                ApplicationIdentityResolutionBasis.ExecutablePathAlias,
                wasCreated: false));
    }

    [Fact]
    public void Resolution_RejectsUnknownBasis()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ApplicationIdentityResolution(
                ApplicationId.New(),
                (ApplicationIdentityResolutionBasis)999,
                wasCreated: false));
    }
}
