using Clipensk.Core.Applications;
using Clipensk.Core.Clipboard;
using Clipensk.Storage.Applications;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class SqliteApplicationGroupRepositoryTests
{
    private const string First = "10000000-0000-0000-0000-000000000001";
    private const string Second = "20000000-0000-0000-0000-000000000002";
    private const string Group = "30000000-0000-0000-0000-000000000003";
    private const string Created = "2026-09-23T10:00:00.0000000+00:00";

    [Fact]
    public async Task EmptyStorage_ReadsAnEmptyDirectory()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();

        ApplicationGroupDirectory directory = await Repository(environment).ReadAsync();

        Assert.Empty(directory.Groups);
        Assert.True(directory.IsInDefaultGroup(new ApplicationId(Guid.Parse(First))));
    }

    [Fact]
    public async Task PersistedGroups_ResolveMembersNamesAndStandalonePolicies()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        InsertIdentities(environment, First, Second);
        environment.Execute($"""
            INSERT INTO ApplicationGroup VALUES ('{Group}', 'Браузеры', 'БРАУЗЕРЫ', 'Allow', '{Created}');
            INSERT INTO ApplicationGroupFormatCapturePolicy VALUES ('{Group}', 'Text', 'Allow', 4096);
            INSERT INTO ApplicationGroupFormatCapturePolicy VALUES ('{Group}', 'HTML Format', 'Deny', NULL);
            INSERT INTO ApplicationGroupMembership VALUES ('{First}', '{Group}', '{Created}');
            """);

        ApplicationGroupDirectory directory = await Repository(environment).ReadAsync();

        ApplicationGroup group = Assert.Single(directory.Groups);
        Assert.Equal(new ApplicationGroupId(Guid.Parse(Group)), group.GroupId);
        Assert.Equal("Браузеры", group.Name.Value);
        Assert.Equal(DateTimeOffset.Parse(Created), group.CreatedAtUtc);
        Assert.Equal(ClipboardCapturePolicyRule.Allow, group.Policy.Capture);
        Assert.Equal(new ClipboardFormatCapturePolicy(ClipboardCapturePolicyRule.Allow, 4096), group.Policy.Formats["Text"]);
        Assert.Equal(new ClipboardFormatCapturePolicy(ClipboardCapturePolicyRule.Deny), group.Policy.Formats["HTML Format"]);
        Assert.Same(group, directory.GroupOf(new ApplicationId(Guid.Parse(First))));
        Assert.True(directory.IsInDefaultGroup(new ApplicationId(Guid.Parse(Second))));
    }

    [Theory]
    [InlineData($"INSERT INTO ApplicationGroup VALUES ('{Group}', 'Office', 'office', 'Allow', '{Created}');")]
    [InlineData($"INSERT INTO ApplicationGroup VALUES ('{Group}', ' Office', ' OFFICE', 'Allow', '{Created}');")]
    [InlineData($"INSERT INTO ApplicationGroup VALUES ('{Group}', 'Office', 'OFFICE', 'Allow', '2026-09-23T13:00:00.0000000+03:00');")]
    [InlineData($"INSERT INTO ApplicationGroup VALUES ('30000000-0000-0000-0000-00000000000A', 'Office', 'OFFICE', 'Allow', '{Created}');")]
    [InlineData($"INSERT INTO ApplicationGroupFormatCapturePolicy VALUES ('{Group}', 'Text', 'Allow', NULL);")]
    [InlineData($"INSERT INTO ApplicationGroupMembership VALUES ('{First}', '{Group}', '{Created}');")]
    public async Task InvalidPersistedGroupData_FailsClosed(string sql)
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        InsertIdentities(environment, First, Second);
        // Foreign keys are switched off for this raw connection so that orphaned rows can be planted.
        environment.Execute("PRAGMA foreign_keys = OFF;\n" + sql);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await Repository(environment).ReadAsync());
    }

    [Fact]
    public async Task Repository_RequiresTheGroupSchema()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.DowngradeToV11();

        await Assert.ThrowsAsync<InvalidDataException>(async () => await Repository(environment).ReadAsync());
    }

    [Fact]
    public async Task RevokedSession_CancelsBeforeOpeningTheDatabase()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Factory.Modes.Clear();
        Assert.True(environment.Lifecycle.TryBeginLock());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Repository(environment).ReadAsync());
        Assert.Empty(environment.Factory.Modes);
    }

    private static SqliteApplicationGroupRepository Repository(GlobalPolicyTestEnvironment environment) =>
        new(environment.Session, environment.Factory);

    private static void InsertIdentities(GlobalPolicyTestEnvironment environment, params string[] applicationIds)
    {
        foreach (string applicationId in applicationIds)
        {
            environment.Execute($"""
                INSERT INTO ApplicationIdentity (ApplicationId, CreatedAtUtc)
                VALUES ('{applicationId}', '{Created}');
                """);
        }
    }
}
