using Clipensk.Core.Applications;
using ClipenskApplicationId = Clipensk.Core.Applications.ApplicationId;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class RepositoryApplicationIdentityRegistryTests
{
    [Fact]
    public async Task ResolveOrCreateAsync_ReturnsNullWithoutDurableEvidence()
    {
        var repository = new StubRepository();
        var registry = new RepositoryApplicationIdentityRegistry(repository);

        ApplicationIdentityResolution? result = await registry.ResolveOrCreateAsync(
            new ApplicationIdentityObservation(null, null));

        Assert.Null(result);
        Assert.Equal(0, repository.FindCount);
        Assert.Equal(0, repository.CreateCount);
        Assert.Equal(0, repository.BindPathCount);
    }

    [Fact]
    public async Task ResolveOrCreateAsync_PrefersKnownAumidAndBindsNewPathAlias()
    {
        ClipenskApplicationId id = ClipenskApplicationId.New();
        var repository = new StubRepository
        {
            Lookup = new ApplicationIdentityAliasLookup(id, null),
        };
        var registry = new RepositoryApplicationIdentityRegistry(repository);
        var observation = new ApplicationIdentityObservation(
            "Contoso.App_123!App",
            "C:\\Apps\\Contoso.exe");

        ApplicationIdentityResolution result = Assert.IsType<ApplicationIdentityResolution>(
            await registry.ResolveOrCreateAsync(observation));

        Assert.Equal(id, result.ApplicationId);
        Assert.Equal(ApplicationIdentityResolutionBasis.PackagedApplicationUserModelId, result.Basis);
        Assert.False(result.WasCreated);
        Assert.Equal(1, repository.FindCount);
        Assert.Equal(0, repository.CreateCount);
        Assert.Equal(1, repository.BindPathCount);
        Assert.Equal(id, repository.BoundPathApplicationId);
        Assert.Equal(observation.ExecutablePath, repository.BoundPath);
    }

    [Fact]
    public async Task ResolveOrCreateAsync_UsesKnownAumidWhenBothAliasesAlreadyAgree()
    {
        ClipenskApplicationId id = ClipenskApplicationId.New();
        var repository = new StubRepository
        {
            Lookup = new ApplicationIdentityAliasLookup(id, id),
        };
        var registry = new RepositoryApplicationIdentityRegistry(repository);

        ApplicationIdentityResolution result = Assert.IsType<ApplicationIdentityResolution>(
            await registry.ResolveOrCreateAsync(
                new ApplicationIdentityObservation(
                    "Contoso.App_123!App",
                    "C:\\Apps\\Contoso.exe")));

        Assert.Equal(id, result.ApplicationId);
        Assert.Equal(ApplicationIdentityResolutionBasis.PackagedApplicationUserModelId, result.Basis);
        Assert.False(result.WasCreated);
        Assert.Equal(0, repository.CreateCount);
        Assert.Equal(0, repository.BindPathCount);
    }

    [Fact]
    public async Task ResolveOrCreateAsync_RejectsAliasesBoundToDifferentApplications()
    {
        ClipenskApplicationId aumidId = ClipenskApplicationId.New();
        ClipenskApplicationId pathId = ClipenskApplicationId.New();
        var repository = new StubRepository
        {
            Lookup = new ApplicationIdentityAliasLookup(aumidId, pathId),
        };
        var registry = new RepositoryApplicationIdentityRegistry(repository);
        var observation = new ApplicationIdentityObservation(
            "Contoso.App_123!App",
            "C:\\Apps\\Contoso.exe");

        ApplicationIdentityConflictException error = await Assert.ThrowsAsync<ApplicationIdentityConflictException>(
            async () =>
            {
                await registry.ResolveOrCreateAsync(observation);
            });

        Assert.Equal(observation, error.Observation);
        Assert.Equal(aumidId, error.ApplicationUserModelIdApplicationId);
        Assert.Equal(pathId, error.ExecutablePathApplicationId);
        Assert.Equal(0, repository.CreateCount);
        Assert.Equal(0, repository.BindPathCount);
    }

    [Fact]
    public async Task ResolveOrCreateAsync_RejectsNewAumidWhenPathAlreadyBelongsToAnotherIdentity()
    {
        ClipenskApplicationId pathId = ClipenskApplicationId.New();
        var repository = new StubRepository
        {
            Lookup = new ApplicationIdentityAliasLookup(null, pathId),
        };
        var registry = new RepositoryApplicationIdentityRegistry(repository);

        await Assert.ThrowsAsync<ApplicationIdentityConflictException>(async () =>
        {
            await registry.ResolveOrCreateAsync(
                new ApplicationIdentityObservation(
                    "Contoso.App_123!App",
                    "C:\\Apps\\Contoso.exe"));
        });

        Assert.Equal(0, repository.CreateCount);
        Assert.Equal(0, repository.BindPathCount);
    }

    [Fact]
    public async Task ResolveOrCreateAsync_CreatesPackagedIdentityWhenAliasesAreUnknown()
    {
        ClipenskApplicationId createdId = ClipenskApplicationId.New();
        var repository = new StubRepository
        {
            Lookup = new ApplicationIdentityAliasLookup(null, null),
            CreatedApplicationId = createdId,
        };
        var registry = new RepositoryApplicationIdentityRegistry(repository);
        var observation = new ApplicationIdentityObservation(
            "Contoso.App_123!App",
            "C:\\Apps\\Contoso.exe");

        ApplicationIdentityResolution result = Assert.IsType<ApplicationIdentityResolution>(
            await registry.ResolveOrCreateAsync(observation));

        Assert.Equal(createdId, result.ApplicationId);
        Assert.Equal(ApplicationIdentityResolutionBasis.PackagedApplicationUserModelId, result.Basis);
        Assert.True(result.WasCreated);
        Assert.Equal(1, repository.CreateCount);
        Assert.Equal(observation, repository.CreatedObservation);
        Assert.Equal(
            ApplicationIdentityResolutionBasis.PackagedApplicationUserModelId,
            repository.CreatedBasis);
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ReturnsKnownPathOnlyIdentity()
    {
        ClipenskApplicationId id = ClipenskApplicationId.New();
        var repository = new StubRepository
        {
            Lookup = new ApplicationIdentityAliasLookup(null, id),
        };
        var registry = new RepositoryApplicationIdentityRegistry(repository);

        ApplicationIdentityResolution result = Assert.IsType<ApplicationIdentityResolution>(
            await registry.ResolveOrCreateAsync(
                new ApplicationIdentityObservation(null, "C:\\Apps\\Classic.exe")));

        Assert.Equal(id, result.ApplicationId);
        Assert.Equal(ApplicationIdentityResolutionBasis.ExecutablePathAlias, result.Basis);
        Assert.False(result.WasCreated);
        Assert.Equal(0, repository.CreateCount);
    }

    [Fact]
    public async Task ResolveOrCreateAsync_CreatesPathOnlyIdentityWhenAliasIsUnknown()
    {
        ClipenskApplicationId createdId = ClipenskApplicationId.New();
        var repository = new StubRepository
        {
            Lookup = new ApplicationIdentityAliasLookup(null, null),
            CreatedApplicationId = createdId,
        };
        var registry = new RepositoryApplicationIdentityRegistry(repository);
        var observation = new ApplicationIdentityObservation(
            null,
            "C:\\Apps\\Classic.exe");

        ApplicationIdentityResolution result = Assert.IsType<ApplicationIdentityResolution>(
            await registry.ResolveOrCreateAsync(observation));

        Assert.Equal(createdId, result.ApplicationId);
        Assert.Equal(ApplicationIdentityResolutionBasis.ExecutablePathAlias, result.Basis);
        Assert.True(result.WasCreated);
        Assert.Equal(1, repository.CreateCount);
        Assert.Equal(observation, repository.CreatedObservation);
        Assert.Equal(
            ApplicationIdentityResolutionBasis.ExecutablePathAlias,
            repository.CreatedBasis);
    }

    [Fact]
    public async Task ResolveOrCreateAsync_HonorsCancellationBeforeRepositoryAccess()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var repository = new StubRepository();
        var registry = new RepositoryApplicationIdentityRegistry(repository);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await registry.ResolveOrCreateAsync(
                new ApplicationIdentityObservation(null, "C:\\Apps\\Classic.exe"),
                cts.Token);
        });

        Assert.Equal(0, repository.FindCount);
    }

    private sealed class StubRepository : IApplicationIdentityRepository
    {
        public ApplicationIdentityAliasLookup Lookup { get; set; } = new(null, null);

        public ClipenskApplicationId CreatedApplicationId { get; set; } = ClipenskApplicationId.New();

        public int FindCount { get; private set; }

        public int CreateCount { get; private set; }

        public int BindPathCount { get; private set; }

        public ApplicationIdentityObservation? CreatedObservation { get; private set; }

        public ApplicationIdentityResolutionBasis? CreatedBasis { get; private set; }

        public ClipenskApplicationId? BoundPathApplicationId { get; private set; }

        public string? BoundPath { get; private set; }

        public ValueTask<ApplicationIdentityAliasLookup> FindAliasesAsync(
            ApplicationIdentityObservation observation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FindCount++;
            return ValueTask.FromResult(Lookup);
        }

        public ValueTask<ClipenskApplicationId> CreateAndBindAsync(
            ApplicationIdentityObservation observation,
            ApplicationIdentityResolutionBasis basis,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCount++;
            CreatedObservation = observation;
            CreatedBasis = basis;
            return ValueTask.FromResult(CreatedApplicationId);
        }

        public ValueTask BindExecutablePathAliasAsync(
            ClipenskApplicationId applicationId,
            string executablePath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BindPathCount++;
            BoundPathApplicationId = applicationId;
            BoundPath = executablePath;
            return ValueTask.CompletedTask;
        }
    }
}
