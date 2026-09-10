using Clipensk.Core.Applications;
using Clipensk.Storage.Applications;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class SqliteApplicationIdentityReadRepositoryTests
{
    [Fact]
    public async Task ListAsync_ReturnsAllIdentitiesAndAliasesInStableBinaryOrderUsingReadOnlyConnection()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ApplicationId first = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        ApplicationId second = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        InsertIdentity(environment, second, "2026-09-10T01:00:00.0000000+00:00");
        InsertIdentity(environment, first, "2026-09-10T00:00:00.0000000+00:00");
        InsertAlias(environment, second, "ExecutablePath", "C:\\Apps\\zeta.exe");
        InsertAlias(environment, second, "Aumid", "Zeta.App_1!App");
        InsertAlias(environment, second, "ExecutablePath", "C:\\Apps\\Alpha.exe");
        InsertAlias(environment, second, "Aumid", "Alpha.App_1!App");
        environment.Factory.Modes.Clear();
        var repository = new SqliteApplicationIdentityRepository(
            environment.Session,
            environment.Factory);

        IReadOnlyList<ApplicationIdentitySummary> result = await repository.ListAsync();

        Assert.Equal([first, second], result.Select(item => item.ApplicationId));
        Assert.Equal(
            new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero),
            result[0].CreatedAtUtc);
        Assert.Empty(result[0].ApplicationUserModelIds);
        Assert.Empty(result[0].ExecutablePaths);
        Assert.Equal(
            ["Alpha.App_1!App", "Zeta.App_1!App"],
            result[1].ApplicationUserModelIds);
        Assert.Equal(
            ["C:\\Apps\\Alpha.exe", "C:\\Apps\\zeta.exe"],
            result[1].ExecutablePaths);
        Assert.Equal([SqliteOpenMode.ReadOnly], environment.Factory.Modes);
    }

    [Fact]
    public async Task ListAsync_RejectsNonCanonicalApplicationId()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        string nonCanonical = "abcdefab-cdef-abcd-efab-cdefabcdefab".ToUpperInvariant();
        environment.Execute($"""
            INSERT INTO ApplicationIdentity (ApplicationId, CreatedAtUtc)
            VALUES ('{nonCanonical}', '2026-09-10T00:00:00.0000000+00:00');
            """);
        var repository = new SqliteApplicationIdentityRepository(
            environment.Session,
            environment.Factory);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await repository.ListAsync());
    }

    [Fact]
    public async Task ListAsync_RejectsNonUtcCreationTimestamp()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ApplicationId applicationId = ApplicationId.New();
        InsertIdentity(
            environment,
            applicationId,
            "2026-09-10T01:00:00.0000000+01:00");
        var repository = new SqliteApplicationIdentityRepository(
            environment.Session,
            environment.Factory);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await repository.ListAsync());
    }

    [Fact]
    public async Task ListAsync_RejectsEmptyAliasValue()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ApplicationId applicationId = ApplicationId.New();
        InsertIdentity(environment, applicationId, "2026-09-10T00:00:00.0000000+00:00");
        environment.Execute($"""
            INSERT INTO ApplicationIdentityAlias (
                AliasType, AliasValue, ApplicationId, CreatedAtUtc)
            VALUES (
                'Aumid', '', '{applicationId}', '2026-09-10T00:00:00.0000000+00:00');
            """);
        var repository = new SqliteApplicationIdentityRepository(
            environment.Session,
            environment.Factory);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await repository.ListAsync());
    }

    [Fact]
    public async Task ListAsync_AfterProtectedAccessRevocationCancelsBeforeRead()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var repository = new SqliteApplicationIdentityRepository(
            environment.Session,
            environment.Factory);
        environment.Factory.Modes.Clear();
        Assert.True(environment.Lifecycle.TryBeginLock());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await repository.ListAsync());

        Assert.Empty(environment.Factory.Modes);
    }

    private static void InsertIdentity(
        GlobalPolicyTestEnvironment environment,
        ApplicationId applicationId,
        string createdAtUtc)
    {
        environment.Execute($"""
            INSERT INTO ApplicationIdentity (ApplicationId, CreatedAtUtc)
            VALUES ('{applicationId}', '{createdAtUtc}');
            """);
    }

    private static void InsertAlias(
        GlobalPolicyTestEnvironment environment,
        ApplicationId applicationId,
        string aliasType,
        string aliasValue)
    {
        environment.Execute($"""
            INSERT INTO ApplicationIdentityAlias (
                AliasType, AliasValue, ApplicationId, CreatedAtUtc)
            VALUES (
                '{aliasType}', '{aliasValue}', '{applicationId}',
                '2026-09-10T00:00:00.0000000+00:00');
            """);
    }
}
