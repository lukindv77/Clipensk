using Clipensk.Core.Applications;
using Clipensk.Core.Clipboard;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedStorageCurrentSchemaV12MigrationTests
{
    private const string Chrome = "10000000-0000-0000-0000-000000000001";
    private const string OtherChrome = "20000000-0000-0000-0000-000000000002";
    private const string Follower = "30000000-0000-0000-0000-000000000003";
    private const string Unassigned = "40000000-0000-0000-0000-000000000004";
    private const string Created = "2026-01-01T00:00:00.0000000+00:00";

    private static readonly ClipboardCapturePolicy Global = new(
        ClipboardCapturePolicyRule.Allow,
        new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal)
        {
            ["Text"] = new(ClipboardCapturePolicyRule.Allow, 1024),
            ["HTML Format"] = new(ClipboardCapturePolicyRule.Allow),
        });

    [Fact]
    public async Task NewStorage_IsCreatedAsV12WithEmptyGroupTablesAndNoV11Table()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();

        Assert.Equal(12, ProtectedStorageDatabaseService.CurrentSchemaVersion);
        Assert.Equal(12, environment.Scalar("PRAGMA user_version;"));
        Assert.Equal(12, environment.Scalar("SELECT SchemaVersion FROM DatabaseIdentity;"));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM ApplicationGroup;"));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM ApplicationGroupMembership;"));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM ApplicationCapturePolicy;"));
        Assert.Equal(0, environment.Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE name = 'ApplicationGroupMember';"));
    }

    [Fact]
    public async Task V11Migration_TurnsLegacyPersonalPoliciesIntoGroupsNamedAfterTheirApplications()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Global);
        environment.DowngradeToV11();
        environment.Execute($"""
            INSERT INTO ApplicationIdentity VALUES
                ('{Chrome}', '{Created}'), ('{OtherChrome}', '{Created}'),
                ('{Follower}', '{Created}'), ('{Unassigned}', '{Created}');
            INSERT INTO ApplicationIdentityAlias VALUES
                ('ExecutablePath', 'C:\Apps\chrome.exe', '{Chrome}', '{Created}'),
                ('ExecutablePath', 'D:\Portable\chrome.exe', '{OtherChrome}', '{Created}');
            INSERT INTO ApplicationCapturePolicy VALUES ('{Chrome}', 'Inherit'), ('{OtherChrome}', 'Deny');
            INSERT INTO ApplicationFormatCapturePolicy VALUES
                ('{Chrome}', 'HTML Format', 'Deny', NULL),
                ('{Chrome}', 'Vendor.Only', 'Inherit', 2048),
                ('{Chrome}', 'Vendor.Allowed', 'Allow', NULL);
            INSERT INTO ApplicationGroupMember VALUES ('{Follower}', '{Chrome}', NULL, '{Created}');
            """);

        Assert.True((await environment.ValidateAsync()).IsSuccess);

        Assert.Equal(12, environment.Scalar("PRAGMA user_version;"));
        Assert.Equal(0, environment.Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('ApplicationGroupMember', 'IX_ApplicationGroupMember_ParentApplicationId');"));
        Assert.Equal(2, environment.Scalar("SELECT COUNT(*) FROM ApplicationCapturePolicy;"));

        ApplicationGroupDirectory groups = ApplicationGroupTestData.ReadGroups(environment);
        ApplicationGroup chrome = groups.GroupOf(Id(Chrome))!;
        ApplicationGroup other = groups.GroupOf(Id(OtherChrome))!;
        Assert.Equal("chrome.exe", chrome.Name.Value);
        Assert.Equal("chrome.exe (2)", other.Name.Value);
        Assert.Same(chrome, groups.GroupOf(Id(Follower)));
        Assert.True(groups.IsInDefaultGroup(Id(Unassigned)));

        // Exactly the policy capture applied before: Merge(global, personal) with anything not
        // explicitly allowed stored as Deny.
        Assert.Equal(ClipboardCapturePolicyRule.Allow, chrome.Policy.Capture);
        Assert.Equal(new ClipboardFormatCapturePolicy(ClipboardCapturePolicyRule.Allow, 1024), chrome.Policy.Formats["Text"]);
        Assert.Equal(new ClipboardFormatCapturePolicy(ClipboardCapturePolicyRule.Deny), chrome.Policy.Formats["HTML Format"]);
        Assert.Equal(new ClipboardFormatCapturePolicy(ClipboardCapturePolicyRule.Deny, 2048), chrome.Policy.Formats["Vendor.Only"]);
        Assert.Equal(new ClipboardFormatCapturePolicy(ClipboardCapturePolicyRule.Allow), chrome.Policy.Formats["Vendor.Allowed"]);
        Assert.Equal(ClipboardCapturePolicyRule.Deny, other.Policy.Capture);
        Assert.Equal(["HTML Format", "Text"], other.Policy.Formats.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task V11Migration_WithoutAGlobalPolicyKeepsOnlyExplicitPersonalRules()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.DowngradeToV11();
        environment.Execute($"""
            INSERT INTO ApplicationIdentity VALUES ('{Chrome}', '{Created}');
            INSERT INTO ApplicationCapturePolicy VALUES ('{Chrome}', 'Inherit');
            INSERT INTO ApplicationFormatCapturePolicy VALUES
                ('{Chrome}', 'Text', 'Allow', 512),
                ('{Chrome}', 'HTML Format', 'Inherit', NULL);
            """);

        Assert.True((await environment.ValidateAsync()).IsSuccess);

        ApplicationGroup group = ApplicationGroupTestData.ReadGroups(environment).GroupOf(Id(Chrome))!;
        Assert.Equal(Chrome, group.Name.Value);
        Assert.Equal(ClipboardCapturePolicyRule.Deny, group.Policy.Capture);
        Assert.Equal(new ClipboardFormatCapturePolicy(ClipboardCapturePolicyRule.Allow, 512), group.Policy.Formats["Text"]);
        Assert.Equal(new ClipboardFormatCapturePolicy(ClipboardCapturePolicyRule.Deny), group.Policy.Formats["HTML Format"]);
    }

    [Fact]
    public async Task V11Migration_WithoutPersonalPoliciesOnlyReplacesTheTables()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.DowngradeToV11();
        environment.Execute($"INSERT INTO ApplicationIdentity VALUES ('{Unassigned}', '{Created}');");

        Assert.True((await environment.ValidateAsync()).IsSuccess);

        Assert.Equal(12, environment.Scalar("PRAGMA user_version;"));
        Assert.Empty(ApplicationGroupTestData.ReadGroups(environment).Groups);
    }

    [Theory]
    [InlineData("DROP INDEX IX_ApplicationGroupMembership_GroupId;")]
    [InlineData("""
        DROP TABLE ApplicationGroupMembership;
        DROP TABLE ApplicationGroupFormatCapturePolicy;
        DROP TABLE ApplicationGroup;
        CREATE TABLE ApplicationGroup (
            GroupId TEXT NOT NULL PRIMARY KEY,
            Name TEXT NOT NULL CHECK (length(Name) > 0),
            NameKey TEXT NOT NULL,
            CaptureRule TEXT NOT NULL CHECK (CaptureRule IN ('Allow', 'Deny')),
            CreatedAtUtc TEXT NOT NULL);
        CREATE TABLE ApplicationGroupFormatCapturePolicy (
            GroupId TEXT NOT NULL,
            FormatName TEXT NOT NULL CHECK (length(FormatName) > 0),
            CaptureRule TEXT NOT NULL CHECK (CaptureRule IN ('Allow', 'Deny')),
            MaxBytes INTEGER NULL CHECK (MaxBytes IS NULL OR MaxBytes > 0),
            PRIMARY KEY (GroupId, FormatName),
            FOREIGN KEY (GroupId) REFERENCES ApplicationGroup(GroupId));
        CREATE TABLE ApplicationGroupMembership (
            ApplicationId TEXT NOT NULL PRIMARY KEY,
            GroupId TEXT NOT NULL,
            JoinedAtUtc TEXT NOT NULL,
            FOREIGN KEY (ApplicationId) REFERENCES ApplicationIdentity(ApplicationId),
            FOREIGN KEY (GroupId) REFERENCES ApplicationGroup(GroupId));
        CREATE INDEX IX_ApplicationGroupMembership_GroupId ON ApplicationGroupMembership(GroupId);
        """)]
    [InlineData("""
        DROP TABLE ApplicationGroupMembership;
        CREATE TABLE ApplicationGroupMembership (
            ApplicationId TEXT NOT NULL PRIMARY KEY,
            GroupId TEXT NOT NULL,
            JoinedAtUtc TEXT NOT NULL,
            FOREIGN KEY (ApplicationId) REFERENCES ApplicationIdentity(ApplicationId) ON DELETE CASCADE,
            FOREIGN KEY (GroupId) REFERENCES ApplicationGroup(GroupId));
        CREATE INDEX IX_ApplicationGroupMembership_GroupId ON ApplicationGroupMembership(GroupId);
        """)]
    public async Task TamperedV12GroupTables_FailValidation(string tamper)
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Execute(tamper);

        Assert.Equal(
            ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity,
            (await environment.ValidateAsync()).Status);
    }

    private static ApplicationId Id(string value) => new(Guid.Parse(value));
}
