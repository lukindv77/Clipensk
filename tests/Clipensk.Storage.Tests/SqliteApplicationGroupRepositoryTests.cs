using Clipensk.Core.Applications;
using Clipensk.Storage.Applications;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class SqliteApplicationGroupRepositoryTests
{
    private const string Parent = "10000000-0000-0000-0000-000000000001";
    private const string Child = "20000000-0000-0000-0000-000000000002";
    private const string Retained = "30000000-0000-0000-0000-000000000003";
    private const string Standalone = "40000000-0000-0000-0000-000000000004";
    private const string Joined = "2026-09-22T10:00:00.0000000+00:00";

    [Fact]
    public async Task EmptyStorage_ReadsAnEmptySnapshot()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();

        ApplicationGroupSnapshot snapshot = await Repository(environment).ReadAsync();

        Assert.Empty(snapshot.Memberships);
        var standalone = new ApplicationId(Guid.Parse(Standalone));
        Assert.Equal(standalone, snapshot.RootOf(standalone));
    }

    [Fact]
    public async Task PersistedGroup_ResolvesRootsMembersAndPersonalPolicy()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        InsertIdentities(environment, Parent, Child, Retained, Standalone);
        environment.Execute($"""
            INSERT INTO ApplicationCapturePolicy (ApplicationId, CaptureRule) VALUES ('{Parent}', 'Allow');
            INSERT INTO ApplicationGroupMember (ApplicationId, ParentApplicationId, RetainedFromApplicationId, JoinedAtUtc)
            VALUES ('{Child}', '{Parent}', NULL, '{Joined}'),
                   ('{Retained}', '{Parent}', '{Standalone}', '{Joined}');
            """);

        ApplicationGroupSnapshot snapshot = await Repository(environment).ReadAsync();

        var parent = new ApplicationId(Guid.Parse(Parent));
        var child = new ApplicationId(Guid.Parse(Child));
        var retained = new ApplicationId(Guid.Parse(Retained));
        var standalone = new ApplicationId(Guid.Parse(Standalone));
        Assert.Equal(parent, snapshot.RootOf(child));
        Assert.Equal([parent, child, retained], snapshot.MembersOf(parent));
        Assert.True(snapshot.IsGovernedByPersonalPolicy(child));
        Assert.False(snapshot.IsGovernedByPersonalPolicy(standalone));
        Assert.Equal(standalone, snapshot.GetMembership(retained)!.RetainedFromApplicationId);
        Assert.Equal(DateTimeOffset.Parse(Joined), snapshot.GetMembership(child)!.JoinedAtUtc);
    }

    [Theory]
    [InlineData($"INSERT INTO ApplicationGroupMember VALUES ('{Child}', '{Parent}', NULL, '{Joined}'), ('{Standalone}', '{Child}', NULL, '{Joined}');")]
    [InlineData($"INSERT INTO ApplicationGroupMember VALUES ('{Child}', '{Parent}', NULL, '{Joined}'); INSERT INTO ApplicationCapturePolicy VALUES ('{Child}', 'Deny');")]
    [InlineData($"INSERT INTO ApplicationGroupMember VALUES ('{Retained}', '{Parent}', '{Standalone}', '{Joined}'); INSERT INTO ApplicationIdentityAlias VALUES ('ExecutablePath', 'C:\\x.exe', '{Retained}', '{Joined}');")]
    [InlineData($"INSERT INTO ApplicationGroupMember VALUES ('{Child}', '{Parent}', NULL, '2026-09-22T13:00:00.0000000+03:00');")]
    [InlineData($"INSERT INTO ApplicationIdentity VALUES ('20000000-0000-0000-0000-00000000000A', '{Joined}'); INSERT INTO ApplicationGroupMember VALUES ('20000000-0000-0000-0000-00000000000A', '{Parent}', NULL, '{Joined}');")]
    public async Task InvalidPersistedGroupData_FailsClosed(string sql)
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        InsertIdentities(environment, Parent, Child, Retained, Standalone);
        environment.Execute(sql);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await Repository(environment).ReadAsync());
    }

    [Fact]
    public async Task ForeignKeys_RejectAMembershipOfAnUnknownIdentity()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        InsertIdentities(environment, Parent);

        using SqliteConnection connection = environment.Factory.Open(
            environment.CurrentPath,
            environment.Key,
            SqliteOpenMode.ReadWrite);
        using (SqliteCommand pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }

        using SqliteCommand insert = connection.CreateCommand();
        insert.CommandText = $"INSERT INTO ApplicationGroupMember VALUES ('{Child}', '{Parent}', NULL, '{Joined}');";
        Assert.Throws<SqliteException>(() => insert.ExecuteNonQuery());
    }

    [Fact]
    public async Task DisposedSession_IsRejected()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        SqliteApplicationGroupRepository repository = Repository(environment);
        environment.Session.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await repository.ReadAsync());
    }

    private static SqliteApplicationGroupRepository Repository(GlobalPolicyTestEnvironment environment) =>
        new(environment.Session, environment.Factory);

    private static void InsertIdentities(GlobalPolicyTestEnvironment environment, params string[] applicationIds)
    {
        foreach (string applicationId in applicationIds)
        {
            environment.Execute($"""
                INSERT INTO ApplicationIdentity (ApplicationId, CreatedAtUtc)
                VALUES ('{applicationId}', '{Joined}');
                """);
        }
    }
}
